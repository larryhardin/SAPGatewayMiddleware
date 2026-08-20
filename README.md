# SAPGateway

ASP.NET Core (.NET 10) gateway middleware that proxies requests to an SAP system,
inspects and validates the XML payload **before converting it to JSON**, and sets
the HTTP status code **only after the full payload has been read**.

## Run

```bash
dotnet run --project src/SapGateway
```

Open the printed URL (e.g. http://localhost:5xxx) — the built-in UI lets you
pick a **destination** and trigger each scenario, then shows the status code,
messages and JSON payload.

## SAP destinations

Destinations are named entries under `Sap:Destinations` in
[appsettings.json](src/SapGateway/appsettings.json). Requests are routed by the
first path segment: `/sap/{destination}/**`.

```json
"Destinations": [
  { "Name": "self", "Description": "Built-in mock SAP system", "BaseUrl": "self" },
  {
    "Name": "EQ5",
    "BaseUrl": "https://sap-eq5.example.com:44300/sap/opu/odata/sap",
    "UserName": "${SAP_EQ5_USER}",
    "Password": "${SAP_EQ5_PASSWORD}",
    "DefaultHeaders": { "x-sap-client": "100" }
  }
]
```

- **Basic auth**: credentials are applied to the upstream call as an
  `Authorization: Basic ...` header. The caller's `Authorization` header is never
  forwarded to SAP.
- **Secrets**: `${ENV_VAR}` placeholders are expanded from the process
  environment, so no password needs to be committed. `GET /api/destinations`
  (used by the UI) never exposes credentials.
- The special `BaseUrl` value `"self"` routes to the built-in mock SAP endpoints
  under `/mock-sap/**`, so everything works without a real SAP system.

## Scenarios (mock SAP)

| UI selection   | Upstream behavior                                  | Gateway result |
| -------------- | -------------------------------------------------- | -------------- |
| `valid`        | Captured sales document response (sales-document.xml) | 200 + JSON payload |
| `invalid-char` | XML containing `0x0B` (vertical tab) in text nodes | 502 + exact byte offsets of every violation |
| `malformed`    | Unclosed tags                                      | 502 + XmlException line/position |
| `error`        | SAP replies HTTP 500 with an XML error body        | 500 passed through, body validated + transformed |
| `large`        | ~2 MB catalog                                      | Streaming test — watch memory stay flat |

## Architecture

```
client → /sap/{destination}/** → SapGatewayMiddleware
                     ├─ HttpClient (ResponseHeadersRead, destination basic auth) → SAP
                     ├─ InspectingStream        ← scans bytes in place, zero copies
                     │     └─ XmlInspectionLog  ← SearchValues<byte> SIMD scan for
                     │                            0x00-0x1F except TAB/LF/CR
                     ├─ XmlReader               ← XML 1.0 well-formedness (throws XmlException)
                     ├─ XmlToJsonTransformer    ← Utf8JsonWriter → PooledBufferWriter (ArrayPool)
                     └─ status code decided HERE → JSON written once, via WriteRawValue
```

### Why not PipeReader / ArrayPool-alone / Decoder?

- **PipeReader** — excellent for byte-level pipelines, but the consumer here is
  `XmlReader`, which pulls from a `Stream`. Bridging PipeReader → Stream and then
  scanning would add a copy or a second pass. `InspectingStream` achieves the same
  goals (single pass, zero-copy, pooled memory) with the scan fused into the read path.
- **ArrayPool** — used, but as a complement (`PooledBufferWriter`), not an
  alternative: it supplies buffers, it is not a pipeline.
- **System.Text.Decoder** — unnecessary: in UTF-8 every byte `0x00-0x1F` is a
  single-byte character, so invalid C0 controls are caught exactly by a vectorized
  **byte** scan (`SearchValues<byte>.IndexOfAny`). `XmlReader` independently
  re-validates and reports line/position.

### Memory profile

Because the status code must be decided after the payload is read, the transformed
JSON is materialized **once** (pooled buffer). The raw XML is never fully
materialized — it is inspected and consumed as a stream. Sibling arrays buffer at
most one sibling subtree at a time.

## Upstream HTTP behavior ("works in curl, fails in the app")

The gateway proved that a request whose *content* is correct can still fail
against a real SAP ICM dispatcher purely because of **how `HttpClient` transmits
it**. When debugging "curl returns 200 but the gateway gets a 400/502", remember:

- **`Expect: 100-continue`** — .NET's `SocketsHttpHandler` sends this handshake on
  POST/PUT requests with a body; curl does not. SAP's ICM rejects it with a
  **400 HTML error page before the body is ever read**, so the gateway surfaces it
  as an XML-validation failure (`The 'hr' start tag ... does not match the end tag
  of 'body'`). The `sap` HttpClient therefore sets
  `DefaultRequestHeaders.ExpectContinue = false` in
  [Program.cs](src/SapGateway/Program.cs).
- **`Accept-Encoding`** — browsers always send `Accept-Encoding: gzip, deflate`;
  curl does not. If forwarded, SAP returns a **gzip-compressed** body and the XML
  reader chokes on the gzip magic byte (`hexadecimal value 0x1F, is an invalid
  character. Line 1, position 1`). The middleware blocklists `Accept-Encoding` and
  the named client enables `AutomaticDecompression` as a safety net.
- **Browser-only headers** (`Origin`, `Referer`, `User-Agent`, `traceparent`,
  `Cookie`) are never forwarded upstream — see `HopByHopHeaders` in
  [SapGatewayMiddleware.cs](src/SapGateway/Middleware/SapGatewayMiddleware.cs).
- On failure the middleware logs the **exact target URL** (and the outbound body,
  when the debugging aid below is enabled). Compare the logged URL/body against a
  working curl command first. If the logged bytes replay cleanly with curl but
  fail through the gateway, the difference is in transmission behavior (above),
  not in content.

### Debugging aid: logging the outbound body

Live, the outbound request body is **the middleware does not buffer the request body' / 'no buffering in the gateway layer**
(`StreamContent` straight onto the wire). That means a failed call cannot log
what was sent — the bytes are gone. To debug a "what did we actually send?"
question, temporarily re-enable the buffered copy in
[SapGatewayMiddleware.cs](src/SapGateway/Middleware/SapGatewayMiddleware.cs):

1. In `InvokeAsync`, uncomment the block under `DEBUGGING AID` that buffers
   `context.Request.Body` into `outboundBody` (a `MemoryStream` + `byte[]`).
2. In `BuildUpstreamRequest`, replace the `StreamContent` assignment with
   `request.Content = new ByteArrayContent(body)` (there is a comment at the spot).

With that enabled, `TrySendUpstreamAsync` (connection failure) and the
XML-validation warning both log the **exact outbound bytes**, which you can diff
against a known-good request. This is how the `Expect: 100-continue` and
forwarded-browser-header bugs above were found.

**Re-disable it when done** — the copy breaks the zero-copy guarantee. It exists
purely as a temporary diagnostic, not for production.

## JSON mapping conventions

- Every element → object; attributes under `"@attributes"`.
- Text content under `"#text"`.
- Consecutive same-named siblings → JSON array.
- Namespace prefixes kept (`soap:Envelope`).

## Layout

- [Middleware/SapGatewayMiddleware.cs](src/SapGateway/Middleware/SapGatewayMiddleware.cs) — destination routing, proxy, basic auth, status decision
- [Options/SapDestination.cs](src/SapGateway/Options/SapDestination.cs) — named destination model with env-var secret expansion
- [Streams/InspectingStream.cs](src/SapGateway/Streams/InspectingStream.cs) — scan-as-you-read decorator
- [Streams/XmlInspectionLog.cs](src/SapGateway/Streams/XmlInspectionLog.cs) — invalid-byte detection
- [Services/XmlToJsonTransformer.cs](src/SapGateway/Services/XmlToJsonTransformer.cs) — streaming XML → JSON
- [Buffers/PooledBufferWriter.cs](src/SapGateway/Buffers/PooledBufferWriter.cs) — ArrayPool-backed IBufferWriter
- [Endpoints/MockSapEndpoints.cs](src/SapGateway/Endpoints/MockSapEndpoints.cs) — built-in fake SAP
- [wwwroot/index.html](src/SapGateway/wwwroot/index.html) — demo UI
