Input: each tool's arguments are exactly its inputSchema — the declared parameters, plus `body` when the tool declares one.

On success (no `isError`), use `structuredContent` when present, otherwise parse the JSON from the first text content, which is the raw response body. Bodies larger than 256 KiB are returned as text only, with no structured content.

On failure (`isError`) there is no structured content — the text is the error, or `HTTP {status} {reason}` when empty.

List tools paginate through their own limit/skip-style arguments.
