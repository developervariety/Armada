// Streamable HTTP session contract: modelcontextprotocol.io/specification/2025-03-26/basic/transports
const http = require('http');
let generation = 0;
const sessions = new Map();
http.createServer(async (req, res) => {
  const send = (status, body, headers = {}) => { res.writeHead(status, { 'Content-Type': 'application/json', ...headers }); res.end(JSON.stringify(body)); };
  try {
    let raw = ''; for await (const chunk of req) raw += chunk;
    const body = typeof req.body === 'object' ? req.body : JSON.parse(req.body || raw || '{}');
    if (req.headers['x-token'] !== 'session-fixture-token') return send(401, {});
    if (body.method === 'initialize') {
      if (req.headers['mcp-session-id']) return send(400, { error: 'Initialize must omit the expired session' });
      const id = 'session-' + (++generation);
      if (!sessions.has(id)) sessions.set(id, false);
      return send(200, { jsonrpc: '2.0', id: body.id, result: {} }, { 'Mcp-Session-Id': id });
    }
    if (body.method === 'notifications/initialized') return send(202, {});
    const session = req.headers['mcp-session-id'];
    if (!session) return send(400, {});
    if (req.url === '/forbidden') return send(403, {});
    if (req.url === '/always' || !sessions.get(session)) {
      // Expire the first session only; the replacement is usable.
      sessions.set('session-' + (generation + 1), true);
      return send(404, { jsonrpc: '2.0', id: body.id, error: { code: -32001, message: 'Session not found' } });
    }
    return send(200, { jsonrpc: '2.0', id: body.id, result: { content: [{ type: 'text', text: 'memory persisted' }] } });
  } catch (error) { send(500, { error: error.message }); }
}).listen(process.env.PORT);
