# Runtime Capabilities

Embedded, application-independent Flecs/Unity capability registry and adapters. Depends on the Flecs Unity bridge; contains its own reflected schemas under Runtime/Capabilities/Resources. The consuming application registers assets, supplies cameras/target resolution and provides audio-bank bytes when soundfont audio is requested. No application Assets path is required by this package.

Existing Runtime.Capabilities namespaces are retained for source compatibility. This extraction changes the assembly boundary, not the native C ABI or saved source identities. Vendor adapters whose APIs compile into Assembly-CSharp stay in the host _Adapters module, independently gated by import. Vendor source/assets are never copied into this package.
