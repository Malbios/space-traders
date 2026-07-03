module SpaceKids.Server.Index

open Bolero.Html
open Bolero.Server.Html

let page = doctypeHtml {
    head {
        meta { attr.charset "UTF-8" }
        meta { attr.name "viewport"; attr.content "width=device-width, initial-scale=1.0" }
        title { "SpaceKids" }
        ``base`` { attr.href "/" }
        link { attr.rel "stylesheet"; attr.href "css/index.css" }
        script { attr.src "js/blockly-host.js" }
    }
    body {
        div { attr.id "main" }
        // Not `boleroScript`: it hardcodes `_framework/blazor.web.js`, which requires the
        // .NET 8+ unified render-mode hosting model this project isn't using (see
        // docs/decisions.md). The classic WASM bootstrap script is a real physical file.
        script { attr.src "_framework/blazor.webassembly.js" }
    }
}
