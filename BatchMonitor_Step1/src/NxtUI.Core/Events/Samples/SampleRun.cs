namespace NxtUI.Core.Events.Samples;

/// <summary>
/// End-to-end template for a generic C# run reporting its flow through the event model —
/// connect, work spans, produce/consume against Targets, a branch decision, an error, and
/// disconnect. Mirrors the DTSX example in docs/10_DTSX_SQL_Event_Logging.md but in code.
///
/// Not wired into DI — it's a copy-paste starting point. Point it at any
/// <see cref="IRunEventSink"/> (e.g. <see cref="Sql.SqlRunEventSink"/>).
/// </summary>
public static class SampleRun
{
    public static async Task RunAsync(IRunEventSink sink, CancellationToken ct = default)
    {
        // Extract stage runs on its own pipeline; the SP/branch run on the default pipeline.
        var extract = new RunEventReporter(sink, runId: "RUN-42", service: "CustomerLoad", pipeline: "DF Extract");
        var main = new RunEventReporter(sink, runId: "RUN-42", service: "CustomerLoad");

        const string batch = "CUST-2026-07-11-01";

        await main.ConnectAsync("package started", ct);                       // block appears in the UI

        // ── Data flow: read a file, write staging ────────────────────────────
        await using (var act = extract.BeginActivity("DFT Extract", correlationId: batch))
        {
            await extract.ConsumeAsync(@"\\share\in\cust.csv", batch, recordCount: 50_000, source: "Flat File Src", ct: ct);
            // ... transform rows ...
            await extract.ProduceAsync("stg.Customer", batch, recordCount: 50_000, source: "OLE DB Dest", ct: ct);
            act.RecordCount = 50_000;                                          // captured on the finish span
        }                                                                     // → Process span emitted here

        // ── A branch / precedence decision ───────────────────────────────────
        await main.InfoAsync("branch=Load taken (rows>0)", batch, source: "Precedence", ct);

        // ── Trigger a stored procedure (a Process span whose Target is the SP) ─
        await using (var sp = main.BeginActivity("EXEC usp_Transform", correlationId: batch, target: "usp_Transform"))
        {
            sp.RecordCount = 49_985;
        }

        // ── Load stage that hits an error ────────────────────────────────────
        var load = new RunEventReporter(sink, runId: "RUN-42", service: "CustomerLoad", pipeline: "DF Load");
        await using (var act = load.BeginActivity("DFT Load", correlationId: batch, target: "dbo.Customer"))
        {
            await load.ConsumeAsync("stg.Customer", batch, recordCount: 49_985, source: "OLE DB Src", ct: ct);
            try
            {
                // ... write to dbo.Customer ...
                throw new InvalidOperationException("PK violation on CustomerId=8842");
            }
            catch (Exception ex)
            {
                await load.ErrorAsync(ex.Message, batch, source: "OLE DB Dest", target: "dbo.Customer", ct: ct);
                act.Fail(ex.Message);                                          // span finishes as Failed
            }
        }

        await main.DisconnectAsync(EventStatus.Failed, "package ended", ct);   // block goes to done/errored
    }
}
