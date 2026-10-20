// Native Responses/embeddings shapes follow PolyPrompt's postman/xai.openapi.json
// and docs.x.ai. Armada and PolyPrompt run outside this HTTP fixture.
const http = require('http');
http.createServer(async (req, res) => {
  const send = (status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  try {
    if (req.headers.authorization !== 'Bearer test-token') return send(401, { error: { message: 'Expected test bearer token' } });
    let raw = ''; for await (const chunk of req) raw += chunk;
    const body = typeof req.body === 'object' ? req.body : JSON.parse(req.body || raw || '{}');
    if (!body.model) return send(400, { error: { message: 'Model required' } });
    // Ollama chat contract: reasoning and answer share the output-token budget.
    if (req.method === 'POST' && req.url === '/api/chat') {
      const enough = (body.options?.num_predict || 4096) >= 32768;
      return send(200, { model: body.model, done: true, done_reason: enough ? 'stop' : 'length',
        message: { role: 'assistant', content: enough ? '[ARMADA:RESULT] COMPLETE\nImplemented and tested the mission.' : '', thinking: 'Reasoning consumes the response budget.' },
        prompt_eval_count: 100, eval_count: enough ? 200 : 4096 });
    }
    if (req.method === 'POST' && req.url === '/v1/embeddings') {
      if (!body.input) return send(400, { error: { message: 'Input required' } });
      return send(200, { object: 'list', model: body.model, data: [{ index: 0, object: 'embedding', embedding: [1, 2, 3] }], usage: { prompt_tokens: 2, total_tokens: 2 } });
    }
    if (req.method !== 'POST' || req.url !== '/v1/responses') return send(501, { error: { message: 'Outside fixture contract' } });
    if (!body.input || body.messages || body.max_tokens) return send(400, { error: { message: 'Expected native Responses request' } });
    const input = Array.isArray(body.input) ? body.input : [];
    if (body.model === 'recover-incomplete' || body.model === 'always-incomplete') {
      const continued = JSON.stringify(input).includes('Your last response did not complete the task');
      const text = body.model === 'recover-incomplete' && continued ? '[ARMADA:RESULT] COMPLETE\nRecovered after an empty terminal response.' : null;
      const output = text ? [{ type: 'message', id: 'msg_recovery', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text, annotations: [] }] }] : [];
      return send(200, { id: 'resp_recovery', object: 'response', model: body.model, status: 'completed', output, usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } });
    }
    const results = input.filter(item => item.type === 'function_call_output');
    const agent = (body.tools || []).some(tool => tool.name === 'write_file');
    if (agent && results.length && !input.some(item => item.type === 'reasoning' && item.encrypted_content === 'fixture-state'))
      return send(400, { error: { message: 'Reasoning state lost between tool turns' } });
    const output = [{ type: 'reasoning', id: 'reason_fixture', encrypted_content: 'fixture-state', summary: [] }];
    if (agent && !results.length) output.push({ type: 'function_call', id: 'fc_fixture', call_id: 'call_fixture', name: 'write_file', arguments: JSON.stringify({ file_path: 'provider-test.txt', content: 'native xAI tool loop' }), status: 'completed' });
    else output.push({ type: 'message', id: 'msg_fixture', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'pong', annotations: [] }] });
    return send(200, { id: 'resp_fixture', object: 'response', model: body.model, status: 'completed', output, usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } });
  } catch (error) { send(500, { error: { message: error.message } }); }
}).listen(process.env.PORT);
