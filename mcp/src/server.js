import { McpServer } from '@modelcontextprotocol/server'
import { z } from 'zod'

/** One registration path for local and remote clients; handlers may perform async API calls. */
export function createServer(tools, { authorize, remote = false, resourceMetadataUrl } = {}) {
  const server = new McpServer({ name: 'opengym', version: '0.2.0' }, {
    instructions: 'Use preview_session before advising training weights. Nutrition: search the personal catalog first; when absent search Open Food Facts, read the exact candidate, import with its fingerprint if canImport=true, then record the supplied grams. Perform this chain without redundant confirmations for an unambiguous match. Ask when flavor, brand, package, preparation, nutrition or quantity is ambiguous; a sole search result is not proof of identity. Never invent nutrients or grams from pieces. Keep the same source/external ID for import retries and the same entry UUID/payload for diary retries. Product nutrition is per 100 grams; consumption is actual grams. Read an entry and obtain user confirmation before deleting it. Dates use the server process timezone unless supplied explicitly.'
  })
  for (const tool of tools) {
    const scope = tool.scope || 'training.read'
    server.registerTool(tool.name, {
      title: tool.name.replaceAll('_', ' '), description: tool.description,
      inputSchema: z.object(tool.schema).strict(),
      annotations: { readOnlyHint: !tool.write, destructiveHint: !!tool.destructive,
        idempotentHint: !tool.write || !!tool.idempotent, openWorldHint: !!tool.openWorld },
      ...(remote ? { _meta: { securitySchemes: [{ type: 'oauth2', scopes: [scope] }] } } : {})
    }, async params => {
      try {
        if (authorize) await authorize(scope)
        const result = await tool.handler(params || {})
        return { structuredContent: result, content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] }
      } catch (err) {
        const code = err.code || 'ERROR'
        return { isError: true,
          ...(resourceMetadataUrl && ['invalid_token', 'insufficient_scope'].includes(code) ? {
            _meta: { 'mcp/www_authenticate': [`Bearer resource_metadata="${resourceMetadataUrl}", scope="${scope}", error="${code}", error_description="Authorization required for this operation"`] }
          } : {}),
          structuredContent: { code, ...(err.status ? { status: err.status } : {}),
          ...(err.retryAfterSeconds ? { retryAfterSeconds: err.retryAfterSeconds } : {}),
          ...(err.uncertain ? { outcome: 'unknown', retry_same_id: true } : {}) },
          content: [{ type: 'text', text: `${code}: ${err.message}` }] }
      }
    })
  }
  return server
}
