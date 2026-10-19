#!/usr/bin/env node
// fworker-mcp: the stdio MCP server the Copilot cloud agent runs (docs/design/copilot-cloud-agent.md part 2). It proxies stdin/stdout MCP to the
// gateway's /mcp over HTTPS. No dependencies: Node 18 or later (global fetch). The gateway serves this file at /fworker-mcp.mjs.
//
// Environment: FWORKER_GATEWAY_URL (the gateway's public URL) and FWORKER_BOOTSTRAP_TOKEN (the repository's bootstrap token, b1). The bootstrap
// token can do one thing: exchange the one-time code in the task's prompt for that task's r1 (POST /api/v1/copilot/exchange). Until the agent
// calls fworker_connect with its code, the only tools are fworker_connect and fworker_call; afterwards the gateway's tools of the phase are listed
// (notifications/tools/list_changed is sent), and fworker_call reaches any of them by name for a client that does not reload its tool list.
// Nothing here prints a token; diagnostics go to stderr.
import { createInterface } from 'node:readline';

const gateway = (process.env.FWORKER_GATEWAY_URL || '').replace(/\/+$/, '');
const bootstrap = process.env.FWORKER_BOOTSTRAP_TOKEN || '';
let r1 = '';
let remoteTools = [];
let rpcId = 0;

const local = [
  { name: 'fworker_connect', description: 'Connect to the factory gateway with the one-time code from your task prompt. Call it first, once.',
    inputSchema: { type: 'object', properties: { code: { type: 'string', description: 'the one-time code (c1....) from the prompt' } }, required: ['code'] } },
  { name: 'fworker_call', description: 'Call a factory gateway tool by name (after fworker_connect), for when the gateway tools are not listed.',
    inputSchema: { type: 'object', properties: { name: { type: 'string' }, arguments: { type: 'object' } }, required: ['name'] } },
];

const send = (m) => process.stdout.write(JSON.stringify(m) + '\n');
const reply = (id, result) => send({ jsonrpc: '2.0', id, result });
const fail = (id, code, message) => send({ jsonrpc: '2.0', id, error: { code, message } });
const text = (o, isError = false) => ({ content: [{ type: 'text', text: typeof o === 'string' ? o : JSON.stringify(o) }], isError });

// one JSON-RPC request to the gateway's /mcp (stateless): the answer is JSON or one SSE event
async function upstream(method, params) {
  const res = await fetch(gateway + '/mcp', {
    method: 'POST',
    headers: { Authorization: 'Bearer ' + r1, 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
    body: JSON.stringify({ jsonrpc: '2.0', id: ++rpcId, method, params: params ?? {} }),
  });
  const body = await res.text();
  if (!res.ok) throw new Error(`gateway /mcp answered ${res.status}`);
  const data = [...body.matchAll(/^data: ?(.*)$/gm)].map((m) => m[1]);
  return JSON.parse(data.length ? data[data.length - 1] : body);
}

async function connect(code) {
  if (!gateway || !bootstrap) return text({ ok: false, code: 'not-configured', reason: 'FWORKER_GATEWAY_URL or FWORKER_BOOTSTRAP_TOKEN is not set' }, true);
  const res = await fetch(gateway + '/api/v1/copilot/exchange', {
    method: 'POST', headers: { Authorization: 'Bearer ' + bootstrap, 'Content-Type': 'application/json' }, body: JSON.stringify({ code: String(code ?? '') }),
  });
  const j = await res.json().catch(() => ({}));
  if (!res.ok || !j.token) return text({ ok: false, code: j.code || `http-${res.status}`, reason: j.reason || 'the exchange was refused' }, true);
  r1 = j.token;
  const list = await upstream('tools/list', {});
  remoteTools = list?.result?.tools ?? [];
  send({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' });
  return text({ ok: true, name: j.name, tools: remoteTools.map((t) => t.name) });
}

async function call(name, args) {
  if (!r1) return text({ ok: false, code: 'not-connected', reason: 'call fworker_connect with the code from your prompt first' }, true);
  const r = await upstream('tools/call', { name, arguments: args ?? {} });
  if (r.error) return text({ ok: false, code: 'rpc-error', reason: r.error.message }, true);
  return r.result;
}

async function handle(m) {
  const { id, method, params } = m;
  if (id === undefined || id === null) return;   // notifications (initialized, cancelled) need no answer
  try {
    switch (method) {
      case 'initialize':
        return reply(id, { protocolVersion: params?.protocolVersion || '2025-06-18', capabilities: { tools: { listChanged: true } }, serverInfo: { name: 'fworker-mcp', version: '1.0.0' } });
      case 'ping': return reply(id, {});
      case 'tools/list': return reply(id, { tools: [...local, ...remoteTools] });
      case 'tools/call': {
        const name = params?.name;
        if (name === 'fworker_connect') return reply(id, await connect(params?.arguments?.code));
        if (name === 'fworker_call') return reply(id, await call(params?.arguments?.name, params?.arguments?.arguments));
        return reply(id, await call(name, params?.arguments));
      }
      default:
        if (!r1) return fail(id, -32601, `method ${method} is not available before fworker_connect`);
        { const r = await upstream(method, params); return r.error ? fail(id, r.error.code ?? -32603, r.error.message) : reply(id, r.result); }
    }
  } catch (e) {
    process.stderr.write(`fworker-mcp: ${method}: ${e?.message ?? e}\n`);
    return fail(id, -32603, String(e?.message ?? e));
  }
}

const rl = createInterface({ input: process.stdin });
const pending = new Set();
rl.on('line', (line) => {
  if (!line.trim()) return;
  let m; try { m = JSON.parse(line); } catch { return fail(null, -32700, 'parse error'); }
  const p = handle(m); pending.add(p); p.finally(() => pending.delete(p));
});
rl.on('close', async () => { await Promise.allSettled([...pending]); process.exit(0); });
