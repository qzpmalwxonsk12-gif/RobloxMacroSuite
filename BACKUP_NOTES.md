# Source snapshot



Original local source files are unchanged. This source snapshot excludes credentials, signing keys, local configuration, dependencies, build caches, runtime data, and unrelated research/old backup folders. No build or functionality test was run as part of this upload.

## Excluded paths

- `RobloxMacroSuite/dist/`
- `RobloxMacroSuite/src\RobloxMacro.App\bin/`
- `RobloxMacroSuite/src\RobloxMacro.App\obj/`
- `RobloxMacroSuite/src\RobloxMacro.Core\bin/`
- `RobloxMacroSuite/src\RobloxMacro.Core\obj/`
- `RobloxMacroSuite/src\RobloxMacro.PoC\bin/`
- `RobloxMacroSuite/src\RobloxMacro.PoC\obj/`
- `RobloxMacroSuite/tests\RobloxMacro.Tests\bin/`
- `RobloxMacroSuite/tests\RobloxMacro.Tests\obj/`




## Final source/security review (2026-11-07)

Restored 4 required authored source/assets that an overly broad backup filter had omitted. Source directories named `Models`, `models`, `cache` or `raw` are not treated as runtime data merely because of their names.
