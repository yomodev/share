// purge-stream.js — ES module. Reads the /api/purge-simulate NDJSON response (one JSON
// object per line, newline-delimited) as it streams in, forwarding each line to Blazor
// as soon as it arrives rather than waiting for the whole response — that's what lets
// PurgeDialog show a live progress bar instead of a single end-of-job result.
async function run(url, body, dotNetRef) {
    try {
        const resp = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
        });
        if (!resp.ok || !resp.body) throw new Error(`HTTP ${resp.status}`);

        const reader = resp.body.getReader();
        const decoder = new TextDecoder('utf-8');
        let carry = '';

        while (true) {
            const { done, value } = await reader.read();
            carry += value ? decoder.decode(value, { stream: !done }) : '';

            let nl;
            while ((nl = carry.indexOf('\n')) >= 0) {
                const line = carry.slice(0, nl).trim();
                carry = carry.slice(nl + 1);
                if (line) await dotNetRef.invokeMethodAsync('OnPurgeLine', line);
            }
            if (done) break;
        }
        if (carry.trim()) await dotNetRef.invokeMethodAsync('OnPurgeLine', carry.trim());
    } catch (err) {
        await dotNetRef.invokeMethodAsync('OnPurgeError', String(err?.message ?? err));
    }
}

window.bmPurgeStream = { run };
export default { run };
export { run };
