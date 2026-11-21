# Third-party notices

This NuGet package bundles native builds of
[CHIMERA](https://github.com/jamesondh/chimera-vocal-synth)
at upstream revision `7c0f722e6a375f0acae543364565741d58c100c9`.

CHIMERA is copyrighted by Jameson Hodge and licensed under MIT; see
`licenses/CHIMERA-LICENSE` included in the package.

Native binaries are compiled by GitHub Actions from the pinned upstream source
rather than downloaded from unsigned release hosts.
The CMU pronunciation dictionary is **not** distributed: configure
`ChimeraStreamingOptions.EnglishDictionaryPath` separately.
