# Сторонние компоненты

Собственный код Visitplate распространяется по [MIT](LICENSE). Этот файл перечисляет сторонние компоненты текущего графа зависимостей и включённые шрифты. Их авторы не заявляли о поддержке или одобрении Visitplate.

## NuGet-зависимости приложения

Точные версии и NuGet `contentHash` зафиксированы в [Core lock-файле](src/Visitplate.Core/packages.lock.json) и [App lock-файле](src/Visitplate.App/packages.lock.json). Ниже SHA-256 именно полученных `.nupkg`, а не извлечённых DLL; это отдельная контрольная сумма, не замена NuGet-проверкам.

| Пакет | Версия / лицензия | SHA-256 `.nupkg` |
| --- | --- | --- |
| [PDFsharp-MigraDoc-WPF](https://www.nuget.org/packages/PDFsharp-MigraDoc-WPF/6.2.4) | 6.2.4 / MIT | `a0550d30fd8686d8caa43a8f62557e7fe323c43462bfa21c89d4166734df3c48` |
| [PDFsharp-WPF](https://www.nuget.org/packages/PDFsharp-WPF/6.2.4) | 6.2.4 / MIT | `138109ddeeea62eeb7844b0c8ebf5eb05ec5acd51f464f4dcd8d653b6bd4f807` |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.3) | 8.0.3 / MIT | `e4c498d5a13051b4577a148f1d8c3470167215c507e2392069b75dc61322bb74` |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/8.0.2) | 8.0.2 / MIT | `51f2df1100245f10da54f0bb7e813f277155117777d4fbbab902214e27372606` |
| [PDFiumCore](https://www.nuget.org/packages/PDFiumCore/154.0.8035) | 154.0.8035 / Apache-2.0 | `6f7b3f351cd059ee5c26b99f57d12fe5b5d2cd10fff5228a057ef1655297c830` |
| [bblanchon.PDFium.Win32](https://www.nuget.org/packages/bblanchon.PDFium.Win32/154.0.8035) | 154.0.8035 / Apache-2.0 в NuGet; полный native bundle ниже | `43327317ddf86f1d3d17446a7e3fcf5642bfd66e8f72af9c1ae0dc5379257dda` |

PDFsharp и MigraDoc создают PDF и его вёрстку. Используется Windows/WPF-вариант. Исходники и лицензия зафиксированной версии: [empira/PDFsharp v6.2.4](https://github.com/empira/PDFsharp/tree/v6.2.4), [LICENSE](https://github.com/empira/PDFsharp/blob/v6.2.4/LICENSE).

Microsoft.Extensions-компоненты входят транзитивно. Commit пакета Logging.Abstractions: [`eba546b0f0d448e0176a2222548fd7a2fbf464c0`](https://github.com/dotnet/runtime/tree/eba546b0f0d448e0176a2222548fd7a2fbf464c0); DependencyInjection.Abstractions: [`81cabf2857a01351e5ab578947c7403a5b128ad1`](https://github.com/dotnet/runtime/tree/81cabf2857a01351e5ab578947c7403a5b128ad1). Идентификаторы взяты из метаданных полученных пакетов.

PDFiumCore также объявляет bblanchon.PDFium.Linux и bblanchon.PDFium.macOS 154.0.8035. Они присутствуют в restore-графе для других платформ; для Windows x64 предназначен native asset `runtimes/win-x64/native/pdfium.dll`. При упаковке следует проверять фактический RID-состав, а не переносить весь NuGet-кэш.

### PDFsharp / MigraDoc - уведомление MIT

```text
Copyright (c) 2001-2026 empira Software GmbH, Troisdorf (Cologne Area), Germany

http://docs.pdfsharp.net

MIT License

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

### Microsoft.Extensions - уведомление MIT

Текст включённого в пакеты `LICENSE.TXT`; [исходная лицензия .NET](https://github.com/dotnet/runtime/blob/eba546b0f0d448e0176a2222548fd7a2fbf464c0/LICENSE.TXT).

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Предпросмотр PDF: PDFiumCore и PDFium

PDFiumCore предоставляет generated .NET bindings к PDFium. Используется CPU-растеризация записанного PDF в bitmap, затем штатный WPF BitmapSource. Исходный код wrapper зафиксирован на [`4a0d6452bed8a5b495ec893861493b521b1e09c2`](https://github.com/Dtronix/PDFiumCore/tree/4a0d6452bed8a5b495ec893861493b521b1e09c2). Полный Apache-2.0 текст сохранён в [licenses/pdfiumcore/LICENSE](licenses/pdfiumcore/LICENSE), 11 357 байт, SHA-256 `c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4`; [точный upstream LICENSE](https://github.com/Dtronix/PDFiumCore/blob/4a0d6452bed8a5b495ec893861493b521b1e09c2/LICENSE).

Нативная Windows x64 DLL соответствует [официальному архиву PDFium chromium/8035](https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8035/pdfium-win-x64.tgz): 3 772 597 байт, SHA-256 `61513d611ad200a383456140739be77d156f1e3a2eef22bd89f6c3bda79bdd41`. Его `bin/pdfium.dll` побайтно совпал с NuGet asset: 7 266 816 байт, SHA-256 `ccfac1aad9e78624ebfb3f54f3f4ddb77af6db2f52803f150e2f9876beda49fe`. Managed `PDFiumCore.dll` для net8.0: 175 616 байт, SHA-256 `bf9fd87ed5814aae14a302a6810087ef3ed8eb735e5b70e8a24c3d82b29d25ba`.

У build/distribution scripts Benoit Blanchon собственная MIT-лицензия. Полный PDFium license содержит BSD-style notice и Apache-2.0; включённые библиотеки имеют отдельные условия. Ни NuGet SPDX-поле, ни MIT-лицензия Visitplate не заменяют эти тексты. В `licenses/pdfium154` сохранены неизменённые root LICENSE и все 14 notices из точного архива:

| Файл | Компонент / условия | SHA-256 исходного файла |
| --- | --- | --- |
| [LICENSE](licenses/pdfium154/LICENSE) | Benoit Blanchon / MIT | `8854f4388f1ca13b3ad9baa42e95f5546b4c0b17109c159256d3eca7be39b09b` |
| [pdfium.txt](licenses/pdfium154/licenses/pdfium.txt) | PDFium / BSD-style и Apache-2.0 | `961eacd9633fff6d051db7208b755e9210e30efac7adec3e6a6d52798f0ccf0e` |
| [llvm-libc.txt](licenses/pdfium154/licenses/llvm-libc.txt) | LLVM libc / Apache-2.0 с LLVM exceptions | `3b6226c32e168c83b891d8d6f0d3c29c2116dc3ef93dc93c307b54f279ecf383` |
| [fast_float.txt](licenses/pdfium154/licenses/fast_float.txt) | fast_float / MIT | `bf1b57355feca8fce77ee95f48002f8d4789fb71b30ec7599c06cda4901fbb2b` |
| [icu.txt](licenses/pdfium154/licenses/icu.txt) | ICU / Unicode License V3 и включённые notices | `93679f4389d53b6835d89843f251844fb9bc455b35bab036d3c8e7abe497a47a` |
| [libjpeg_turbo.md](licenses/pdfium154/licenses/libjpeg_turbo.md) | libjpeg-turbo / IJG и modified BSD; SIMD zlib terms объяснены в тексте | `be2b2b5ab168bce87bc3e31f2a5c5adba4b7f6e9e51d618e958d1d46972ebd95` |
| [libjpeg_turbo.ijg](licenses/pdfium154/licenses/libjpeg_turbo.ijg) | Independent JPEG Group / полный IJG notice | `db16a04128171879c60708d171b88d97345a2dd20f9bfc173680a4497c73f704` |
| [freetype.txt](licenses/pdfium154/licenses/freetype.txt) | FreeType / FreeType Project License, FTL | `f4b133e25df1f86ad3ffea453aa0e613f0474f34778dbbb3e437e7b2724937d8` |
| [abseil.txt](licenses/pdfium154/licenses/abseil.txt) | Abseil / Apache-2.0 | `f54fff0b905df5b3464527c652a30e903b172d6dcab4d89b5e6f105d5e4a4603` |
| [lcms.txt](licenses/pdfium154/licenses/lcms.txt) | Little CMS / MIT | `7312b68c5b25e9bf2b828706fb4e29588f22705112f411fd42e1f7d84c3d139a` |
| [simdutf.txt](licenses/pdfium154/licenses/simdutf.txt) | simdutf / MIT | `c172a0ba936ff31230febb5dad869e25cb7c1a07480c7a381be8cf011bb52719` |
| [agg23.txt](licenses/pdfium154/licenses/agg23.txt) | Anti-Grain Geometry 2.3 / permissive notice | `c110d3ea2ad77467ce0dcff7d3337e6c8be8049a5103f4b9bd5fd911a77972e5` |
| [libopenjpeg.txt](licenses/pdfium154/licenses/libopenjpeg.txt) | OpenJPEG / BSD-2-Clause | `c5ab0890a737c2dfa7ba675036554f6d17741d98629b0c2a145354d00617e6b2` |
| [zlib.txt](licenses/pdfium154/licenses/zlib.txt) | zlib / zlib license | `33fd641c9f3b0e0be64bc78fea9e94807674cdd70c48477599226cb8956565fe` |
| [libpng.txt](licenses/pdfium154/licenses/libpng.txt) | libpng / PNG Reference Library License и включённые notices | `452390433ba0f88aa3e2b122c647741b72a0c117cd6ed7a329b49785aecb5511` |

Visitplate использует код [FreeType Project](https://freetype.org/) через PDFium. This software is based in part on the work of the FreeType Team. FreeType предоставляется без гарантий по полному FTL-тексту выше; здесь используется FTL, не альтернативный GPL-вариант.

This software is based in part on the work of the Independent JPEG Group.

Файлы лицензий сохранены побайтно, включая исходные окончания строк и однобайтовую кодировку FreeType notice. Не перекодируйте и не редактируйте их при упаковке. Контрольные суммы подтверждают происхождение файлов, а не юридическую гарантию, отсутствие уязвимостей или независимую воспроизводимую сборку PDFium.

## Включённые шрифты Noto Sans

Источник - официальный [NotoSans-v2.015](https://github.com/notofonts/latin-greek-cyrillic/releases/tag/NotoSans-v2.015), опубликованный 20 ноября 2024 года. Из [архива NotoSans-v2.015.zip](https://github.com/notofonts/latin-greek-cyrillic/releases/download/NotoSans-v2.015/NotoSans-v2.015.zip) включены неизменённые файлы двух начертаний и лицензия.

Архив: 117 491 253 байта, SHA-256 `0c34df072a3fa7efbb7cbf34950e1f971a4447cffe365d3a359e2d4089b958f5`.

| Путь в официальном архиве | Байты | SHA-256 включённого файла |
| --- | ---: | --- |
| `NotoSans/full/ttf/NotoSans-Regular.ttf` | 825 628 | `f5f552c8c5edb61fe6efb824baf4d4de47b1a8689ab4925ff43f7bd6a4ebece5` |
| `NotoSans/full/ttf/NotoSans-Bold.ttf` | 838 072 | `3a08a47daa00cade516425c15c57615aef2fd418ec9811a7b9f465088f92cc05` |
| `OFL.txt` | 4 396 | `cee9892f9f0cc8fe882c9e9537ee6a89621d86ee7ceaf70b02e2b2b1c25c061a` |

Copyright 2022 The Noto Project Authors. Лицензия - SIL Open Font License 1.1. Полный неизменённый текст находится в [src/Visitplate.Core/fonts/OFL.txt](src/Visitplate.Core/fonts/OFL.txt); [upstream OFL.txt](https://github.com/notofonts/latin-greek-cyrillic/blob/NotoSans-v2.015/OFL.txt).

TTF встроены в Core как ресурсы; при подготовке PDF используются полные файлы Regular/Bold. Они не устанавливаются в Windows и не загружаются из сети при работе. OFL разрешает встраивание и совместное распространение при соблюдении её условий; лицензия шрифта не распространяется на созданный пользователем документ. При распространении приложения сохраняйте OFL вместе со шрифтами.

Полное встраивание выбрано после обнаруженного различия отображения поднабора Noto в системном просмотре Windows. Это увеличивает размер PDF, но не является обещанием одинаковых пикселей во всех PDF-просмотрщиках. Шрифт не обеспечивает все письменности и emoji.

## Среда выполнения и инструменты разработки

WPF и JPEG/PNG-кодеки используются как стандартные возможности .NET/Windows; Windows-компоненты не перелицензируются MIT-лицензией Visitplate. При self-contained распространении .NET сохраняйте лицензии и сторонние уведомления соответствующей поставки runtime. Состав runtime определяется публикуемой сборкой, а не этой таблицей NuGet.

Windows x64 поставка включает `Microsoft.NETCore.App` и `Microsoft.WindowsDesktop.App` 10.0.11. Версия runtime закреплена отдельно от SDK. Оба фактически полученных runtime pack указывают исходный commit [`e2f47b0110ed922f21a1522da67279133ce28f32`](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32). Включены полные неизменённые лицензии пакетов, общий runtime notice и отдельные upstream notices WPF/Windows Forms из этого commit:

| Файл | Источник / размер | SHA-256 |
| --- | --- | --- |
| [dotnet10/LICENSE.TXT](licenses/dotnet10/LICENSE.TXT) | [Microsoft.NETCore.App.Runtime.win-x64 10.0.11](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.11), 1 139 байт | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` |
| [dotnet10/THIRD-PARTY-NOTICES.TXT](licenses/dotnet10/THIRD-PARTY-NOTICES.TXT) | Тот же runtime pack, 78 041 байт | `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21` |
| [windowsdesktop10/LICENSE](licenses/windowsdesktop10/LICENSE) | [Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.11](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.11), 1 137 байт | `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab` |
| [windowsdesktop10/wpf-THIRD-PARTY-NOTICES.TXT](licenses/windowsdesktop10/wpf-THIRD-PARTY-NOTICES.TXT) | [WPF notice](https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/src/wpf/THIRD-PARTY-NOTICES.TXT), 2 703 байта | `d23dba2fee20c6b59d8f7088ee9ed372459c952a319b7ca90f3eec370cc76eb9` |
| [windowsdesktop10/winforms-THIRD-PARTY-NOTICES.TXT](licenses/windowsdesktop10/winforms-THIRD-PARTY-NOTICES.TXT) | [Windows Forms notice](https://github.com/dotnet/dotnet/blob/e2f47b0110ed922f21a1522da67279133ce28f32/src/winforms/THIRD-PARTY-NOTICES.TXT), 1 966 байт | `5a412b38efae0b162dc0893b319023f4cd14a8985a0f9f9ae01ac052b37a36eb` |

Общие upstream notices сохранены целиком, без попытки вырезать пункты для других платформ или частей Windows Desktop. Наличие пункта в таком файле не означает, что соответствующая библиотека используется Visitplate как отдельная зависимость. Native-компоненты WPF входят в официальную поставку Windows Desktop; их присутствие не означает GPU-растеризацию PDF. При обновлении runtime нужно заново сопоставить его состав и уведомления, а не оставлять эти хэши от прежней версии.

Тесты используют [MSTest.Sdk 4.4.0](https://www.nuget.org/packages/MSTest.Sdk/4.4.0), MIT, и Microsoft.Testing.Platform. Их отдельный граф находится в [тестовом lock-файле](tests/Visitplate.Core.Tests/packages.lock.json); тестовые библиотеки не являются функцией приложения и не должны добавляться в пользовательскую поставку ради запуска отчётов.

Этот файл, `fonts/OFL.txt` и полный каталог `licenses` должны сопровождать распространяемые бинарные файлы. Изменение версии пакета, native DLL или замена TTF требуют повторной проверки лицензий и контрольных сумм.
