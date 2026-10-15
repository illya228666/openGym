import { McpServer } from '@modelcontextprotocol/server'
import { z } from 'zod'

/** One registration path for local and remote clients; handlers may perform async API calls. */
export function createServer(tools, { authorize, remote = false, resourceMetadataUrl } = {}) {
  const server = new McpServer({ name: 'opengym', version: '0.2.0' }, {
    instructions: 'Use preview_session before advising training weights. Nutrition: search the personal catalog first and reuse a matching product. If absent, search Open Food Facts for packaged products, read the exact candidate and import using its source/external ID/fingerprint if canImport=true. Never infer uniqueness from the first result. For homemade food absent from catalogs, search USDA reference foods, inspect food identity and preparation, then create a custom product from independently verified reference values and call nutrition_create_entry. Never invent nutrients, replace missing data with zeros, use a random industrial product for homemade food, or convert raw mass to cooked without known factors. Preparation analogs require explicit user confirmation; describe reference averages honestly, not as exact lab data. Clarify ambiguous food, state, date or grams; never guess grams from pieces. Preserve one product UUID and entry UUID per user operation and identical payloads on retries; after a repeated natural-language request check the diary and ask if it is a retry or a separate meal when unclear. Use the returned product ID after deduplication. Report partial success if product creation succeeded but diary creation failed. Read an entry and obtain user confirmation before deleting it. Dates use the server process timezone unless supplied explicitly.'
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
