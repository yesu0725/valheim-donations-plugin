// Reserved for shared helpers. The HMAC validator that lived here is gone:
// authentication is now handled at the HTTPS transport layer (bearer token in
// BackendClient), so the plugin no longer needs to verify per-grant signatures.
