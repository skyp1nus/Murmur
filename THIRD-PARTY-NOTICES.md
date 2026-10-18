# Third-party notices

Murmur bundles or links the components below. Each is used under the license shown.

| Component | Version | License | Use |
| --- | --- | --- | --- |
| Inter | 4.x (via Avalonia.Fonts.Inter 12.1.4) | SIL OFL 1.1 | UI font, weights 300/400/500 |
| Geist Mono | 1.8.1 (npm `geist`) | SIL OFL 1.1 | Timestamps and timers, embedded as `src/Murmur.App/Assets/Fonts/GeistMono-Regular.ttf` |
| Avalonia (Avalonia, .Desktop, .Themes.Fluent, .Fonts.Inter, .Skia, .Headless) | 12.1.4 | MIT | UI framework |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM source generators |
| Microsoft.Extensions.AI.Abstractions | 10.11.0 | MIT | `IChatClient` / `IEmbeddingGenerator` abstractions |
| Microsoft.Data.Sqlite (+ SQLitePCLRaw, e_sqlite3) | 11.0.0-rc.2 | MIT (SQLitePCLRaw: Apache-2.0; SQLite: public domain) | Sessions, transcripts, FTS5, vectors |
| System.Numerics.Tensors | 11.0.0-rc.2 | MIT | SIMD dot products for vector search |
| sherpa-onnx native runtime (`org.k2fsa.sherpa.onnx.runtime.<rid>`) | 1.13.8 | Apache-2.0 (bundles ONNX Runtime 1.28: MIT) | ASR worker (`Murmur.Transcription`) |
| llama.cpp `llama-server` (downloaded by `scripts/fetch-models` `-Ai`, not in the repo) | b11716 | MIT | Local LLM / embeddings sidecar |

Models are downloaded at first run / by `scripts/fetch-models`, never committed:

| Model | License | Note |
| --- | --- | --- |
| NVIDIA Parakeet TDT 0.6B v3 (sherpa-onnx int8 export) | CC-BY-4.0 | Attribution required in the About screen |
| Silero VAD | MIT | |
| Gemma 4 E4B instruct (GGUF) | Apache-2.0 (per docs/PLAN.md) | Verify the exact terms of the GGUF repo before release |
| EmbeddingGemma 300M (GGUF) | Gemma Terms of Use | Verify before release; swap for an Apache/MIT embedding model if needed |

## Inter - SIL Open Font License 1.1

    Copyright (c) 2016-2018 The Inter Project Authors (me@rsms.me)

    This Font Software is licensed under the SIL Open Font License, Version 1.1.
    This license is copied below, and is also available with a FAQ at:
    http://scripts.sil.org/OFL

    -----------------------------------------------------------
    SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
    -----------------------------------------------------------

    PREAMBLE
    The goals of the Open Font License (OFL) are to stimulate worldwide
    development of collaborative font projects, to support the font creation
    efforts of academic and linguistic communities, and to provide a free and
    open framework in which fonts may be shared and improved in partnership
    with others.

    The OFL allows the licensed fonts to be used, studied, modified and
    redistributed freely as long as they are not sold by themselves. The
    fonts, including any derivative works, can be bundled, embedded,
    redistributed and/or sold with any software provided that any reserved
    names are not used by derivative works. The fonts and derivatives,
    however, cannot be released under any other type of license. The
    requirement for fonts to remain under this license does not apply
    to any document created using the fonts or their derivatives.

    DEFINITIONS
    "Font Software" refers to the set of files released by the Copyright
    Holder(s) under this license and clearly marked as such. This may
    include source files, build scripts and documentation.

    "Reserved Font Name" refers to any names specified as such after the
    copyright statement(s).

    "Original Version" refers to the collection of Font Software components as
    distributed by the Copyright Holder(s).

    "Modified Version" refers to any derivative made by adding to, deleting,
    or substituting -- in part or in whole -- any of the components of the
    Original Version, by changing formats or by porting the Font Software to a
    new environment.

    "Author" refers to any designer, engineer, programmer, technical
    writer or other person who contributed to the Font Software.

    PERMISSION AND CONDITIONS
    Permission is hereby granted, free of charge, to any person obtaining
    a copy of the Font Software, to use, study, copy, merge, embed, modify,
    redistribute, and sell modified and unmodified copies of the Font
    Software, subject to the following conditions:

    1) Neither the Font Software nor any of its individual components,
    in Original or Modified Versions, may be sold by itself.

    2) Original or Modified Versions of the Font Software may be bundled,
    redistributed and/or sold with any software, provided that each copy
    contains the above copyright notice and this license. These can be
    included either as stand-alone text files, human-readable headers or
    in the appropriate machine-readable metadata fields within text or
    binary files as long as those fields can be easily viewed by the user.

    3) No Modified Version of the Font Software may use the Reserved Font
    Name(s) unless explicit written permission is granted by the corresponding
    Copyright Holder. This restriction only applies to the primary font name as
    presented to the users.

    4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
    Software shall not be used to promote, endorse or advertise any
    Modified Version, except to acknowledge the contribution(s) of the
    Copyright Holder(s) and the Author(s) or with their explicit written
    permission.

    5) The Font Software, modified or unmodified, in part or in whole,
    must be distributed entirely under this license, and must not be
    distributed under any other license. The requirement for fonts to
    remain under this license does not apply to any document created
    using the Font Software.

    TERMINATION
    This license becomes null and void if any of the above conditions are
    not met.

    DISCLAIMER
    THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
    OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
    COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
    INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
    DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
    FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
    OTHER DEALINGS IN THE FONT SOFTWARE.

## Geist Mono - SIL Open Font License 1.1

Copyright (c) 2023 Vercel, in collaboration with basement.studio

The license text is identical to the Inter one above and is shipped next to the font file as
`src/Murmur.App/Assets/Fonts/GeistMono-OFL.txt`.

## Avalonia - MIT

Copyright (c) The Avalonia Project. Licensed under the MIT license (https://licenses.nuget.org/MIT).

## CommunityToolkit.Mvvm - MIT

Copyright (c) .NET Foundation and Contributors. Licensed under the MIT license (https://licenses.nuget.org/MIT).
