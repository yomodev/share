namespace BulkUploader.Core;

/// <summary>
/// Throughput-based hill-climbing batch size tuner.
///
/// After every successful batch upload the uploader calls <see cref="RecordBatch"/>.
/// The tuner computes <c>records / ms</c> and compares it to the previous measurement:
/// <list type="bullet">
///   <item>Improved → keep direction (continue growing or shrinking).</item>
///   <item>Worsened → reverse direction.</item>
///   <item>Within dead-band (noise) → keep probing; after 3 consecutive neutral
///         measurements, make an exploratory jump to escape plateaus.</item>
/// </list>
/// Step size is proportional (<c>current × stepFraction</c>), so the tuner moves
/// faster far from the optimum and slows near it.
///
/// Not thread-safe by design — only ever called from the single uploader task.
/// </summary>
public sealed class BatchSizeTuner
{
    private readonly int    _min;
    private readonly int    _max;
    private readonly double _stepFraction;
    private readonly double _deadBandFraction;
    private readonly int    _minStep;

    private int    _current;
    private int    _direction = +1;                // +1 growing, -1 shrinking
    private double _lastThroughput = double.NegativeInfinity;
    private int    _stableCount;

    /// <summary>Current recommended batch size. Read by the batcher each flush cycle.</summary>
    public int BatchSize => _current;

    /// <param name="initial">Starting batch size.</param>
    /// <param name="min">Hard floor (>= 1).</param>
    /// <param name="max">Hard ceiling (>= min).</param>
    /// <param name="stepFraction">Step = current × stepFraction. Default 0.15 (15%).</param>
    /// <param name="deadBandFraction">Changes &lt; this fraction are treated as noise. Default 0.05 (5%).</param>
    public BatchSizeTuner(
        int    initial          = 500,
        int    min              = 50,
        int    max              = 50_000,
        double stepFraction     = 0.15,
        double deadBandFraction = 0.05)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(min, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        _min              = min;
        _max              = max;
        _stepFraction     = stepFraction;
        _deadBandFraction = deadBandFraction;
        _minStep          = Math.Max(1, (int)(min * stepFraction));
        _current          = Math.Clamp(initial, min, max);
    }

    /// <summary>
    /// Called by the uploader task after every successful batch upload.
    /// Updates the internal model and adjusts <see cref="BatchSize"/> for the next batch.
    /// </summary>
    /// <param name="recordsUploaded">Number of records in the completed batch.</param>
    /// <param name="elapsedMs">Wall-clock time for the upload call only (not drain time).</param>
    public void RecordBatch(int recordsUploaded, double elapsedMs)
    {
        if (elapsedMs <= 0 || recordsUploaded <= 0) return;

        var throughput = recordsUploaded / elapsedMs;
        var delta      = throughput - _lastThroughput;

        if (_lastThroughput == double.NegativeInfinity)
        {
            _lastThroughput = throughput;
            return;
        }

        var threshold = Math.Abs(_lastThroughput * _deadBandFraction);

        if (Math.Abs(delta) < threshold)
        {
            _stableCount++;
            if (_stableCount >= 3)
            {
                _stableCount = 0;
                Nudge(multiplier: 2.0); // exploratory jump to escape plateau
            }
        }
        else if (delta > 0)
        {
            _stableCount    = 0;
            _lastThroughput = throughput;
            Nudge();
        }
        else
        {
            _stableCount    = 0;
            _lastThroughput = throughput;
            _direction     *= -1;
            Nudge();
        }
    }

    /// <summary>
    /// Operator override: forces a specific batch size and resets the baseline
    /// so the tuner resumes hill-climbing from the new value.
    /// </summary>
    public void Override(int batchSize)
    {
        _current        = Math.Clamp(batchSize, _min, _max);
        _lastThroughput = double.NegativeInfinity;
    }

    /// <summary>Returns a diagnostic snapshot. Safe to call from any thread (int reads are atomic).</summary>
    public TunerSnapshot Snapshot() => new(_current, _direction, _lastThroughput);

    private void Nudge(double multiplier = 1.0)
    {
        var step = Math.Max(_minStep, (int)(_current * _stepFraction * multiplier));
        _current = Math.Clamp(_current + _direction * step, _min, _max);
    }
}

/// <summary>Diagnostic snapshot of the tuner state at a point in time.</summary>
public readonly record struct TunerSnapshot(
    int    BatchSize,
    int    Direction,
    double LastThroughputRecordsPerMs);
