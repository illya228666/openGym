import { McpServer } from '@modelcontextprotocol/server'
import { z } from 'zod'

/** One registration path for local and remote clients; handlers may perform async API calls. */
export function createServer(tools, { authorize, remote = false, resourceMetadataUrl } = {}) {
  const server = new McpServer({ name: 'opengym', version: '0.2.0' }, {
    instructions: 'Use preview_session before advising training weights. Nutrition: search the personal catalog first and reuse matching products, including estimates. USDA and Open Food Facts are optional accuracy aids, never prerequisites to logging food. If no saved product matches and the user delegates estimation (including an ordinary request to log homemade food), choose reasonable per-100g values for the stated preparation and create ai_estimate without redundant confirmation or external calls. Use user_provided for values supplied by the user. Disclose estimates as approximate when confirming the saved entry. Only verified_reference claims require independent USDA verification; never label estimates as verified. Keep raw/cooked preparation and eaten grams distinct; do not infer raw-to-cooked yields. Clarify genuinely ambiguous food or grams, not whether estimates are allowed. Preserve stable product/entry UUIDs and identical retry payloads; use the returned product ID and values after deduplication. Call nutrition_create_entry and confirm saving only after it succeeds. Report partial success if only the product was saved. Read an entry and obtain confirmation before deletion. Dates use the process timezone unless explicit.'
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
