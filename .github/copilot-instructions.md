# Copilot Instructions

## Project Guidelines
- For Quiver, entity caching terminology is considered ambiguous; the preferred design direction is that entities themselves should not be described as cached, while marked vectors and large blob fields are the data subject to caching/lazy paging behavior.
- Rename EntityCache concepts toward LargeFieldMemoryMode, and consider renaming QuiverBlobAttribute to QuiverLargeFieldAttribute to reduce ambiguity in terminology.
- For Quiver API redesign, include an Override/Custom-style value in VectorMemoryMode where per-QuiverVector settings are honored, and remove the Lazy property from QuiverVector.