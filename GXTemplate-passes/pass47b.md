# Pass 47b: replace ImageSharp with SkiaSharp (carry-over from ProjectTracking passes 29–30)

**Suites (template solution, each project alone, PostgreSQL 15.7 at 127.0.0.1:5432 via `GX_TEST_PG`):**
Application.UnitTests **568 passed + 14 skipped** · Infrastructure.UnitTests **227** ·
Application.IntegrationTests **60** · Server.UI.IntegrationTests **311** (was 283). 0 failed.

**Date:** 2026-11-03. **Start:** HEAD `a8a89d63` ("WAT Correction"). The tree also held two changes that are not
this pass's: `src/Server.UI/Components/AppShell/TenantSelector.razor` (modified) and
`docs/HANDOFF-tenant-switcher.md` (untracked). Both were left unstaged and out of the commit.
**Commit:** `pass47b`, one commit, not pushed.
**Model:** `C:\Yoab\Projects\GXProjectTracking` (read only): `a888aba` pass29, `ece1631` pass30.

Evidence labels:
- **VERIFIED**: read or run here, and measured.
- **INFERRED**: reasoning not confirmed here.

Line numbers are the committed tree's, unless marked **(before)**, which means HEAD `a8a89d63`. No connection
string or password is printed or committed.

---

## Summary

1. **First, the read-and-report** (below, "What ImageSharp did").
   - **ImageSharp had two uses, not one:** the profile photo (`ImageProcessor`, the model's use) and the
     **Documents upload dialog**, which ProjectTracking removed and the template keeps (D5).
   - **Both could be replaced simply,** so the work went ahead without stopping.
   - **The template never had ProjectTracking's 26a hardening.** Today it decodes any format ImageSharp knows, has
     no pixel limit, and copies the upload's EXIF, GPS included, into both outputs (measured).

   VERIFIED
2. **S1: ImageSharp is replaced by SkiaSharp 4.154.0** (MIT; `Server.UI.csproj:70-72`). Size, format and quality
   are what ImageSharp 3.1.12 produced, measured on the HEAD code before the change:
   - **profile photo:** a 128 × 128 JPEG at quality 80, 4:2:0, scaled to cover and centre-cropped, smaller images
     scaled up;
   - **document image:** a PNG scaled by ImageSharp's own factor and rounding (nine sizes pinned by tests), RGB
     when opaque and RGBA when not.

   VERIFIED
3. **S2: the hardening holds for both uses.** Every check is made before any pixel is decoded, in one shared
   `Decode` (`ImageProcessor.cs:111-152`):
   - **format, by content:** only JPEG, PNG and WebP, from the first bytes (`:118-123`, `FormatOf` `:184-192`).
     The decoder must agree (`:125-131`);
   - **size:** at most 50 megapixels, read from the header (`MaxPixels` `:34`, check `:133-139`);
   - **metadata:** none in either output. Pixels are decoded without a colour space (`:141-148`) and re-encoded;
   - **orientation:** applied from EXIF before the crop or scale (`:150-151`, `Upright` `:158-181`).

   VERIFIED
4. **S3: a refused upload shows its message where the user uploaded, and the photo stays.**
   - **Profile tab** (`ProfileInformationTab.razor:57-61`, `:187-200`) and **administrator's user dialog**
     (`UserFormDialog.razor:43-47`, `:190-202`): the message appears under the photo in a
     `data-test=photo-upload-error` alert, and nothing is stored.
   - **Documents dialog:** its existing `catch` puts the message in a snackbar over the dialog
     (`UploadFilesFormDialog.razor:136-138`, catch unchanged).
   - **No refusal reaches the circuit.**

   VERIFIED
5. **S4: ImageSharp is gone.**
   - **Package:** the reference is removed (`Server.UI.csproj:70` **(before)**), and no transitive path remains.
   - **Usings:** 5 in `Users/Components/_Imports.razor:65-69` **(before)** and 3 in `UploadFilesFormDialog.razor:1-3`
     **(before)** are removed.
   - **Suppressions:** there were none to remove (Disproven premises #3).
   - **`IsImageFile`**, unused and judging by extension (.gif and .bmp included), is removed
     (`ImageProcessor.cs:57-62` **(before)**).

   VERIFIED
6. **S5: stored photos are untouched.**
   - **Serving path:** `FileEndpoints.cs` never used ImageSharp and is not changed. It serves stored bytes as they
     are.
   - **Tests:** `FileEndpointMatrixTests` is green.
   - **Not rendered in a browser** (Found but not fixed #4).

   VERIFIED (read and run)
7. **S6: the NuGet audit flagged three other packages, now pinned at 10.0.14** as ProjectTracking pass 28 did.
   **The server runtime minimum becomes .NET 10 / ASP.NET Core 10.0.13** (Hosting Bundle 10.0.13 or later; 10.0.14
   is current). VERIFIED (audit); the 10.0.13 fix version is INFERRED from ProjectTracking pass 28's reading of the
   advisories
8. **S7:** `README.md:168-176` now says that the pool must run 64-bit and gives the Hosting Bundle minimum. VERIFIED
9. **Checks:** X1–X4 each went red at its named test. Every file was restored byte-identical. No test host aborted.
   VERIFIED
10. **Controls:**
    - **Build:** 0 errors; the 11 code warnings identical to HEAD's; no NuGet audit warning (336 audit lines at HEAD),
      with no suppression anywhere.
    - **Packages:** no `SixLabors.ImageSharp` in any project; `SixLabors.Fonts` 1.0.0 remains, via ClosedXML.
    - **Suites:** all green on PostgreSQL.
    - **Smoke:** see Controls #4.
    - **No migration.**

    VERIFIED

---

## What ImageSharp did (read before changing anything)

| Where **(before)** | What |
|---|---|
| `src/Server.UI/Server.UI.csproj:70` | `SixLabors.ImageSharp` 3.1.12, the only reference. Nothing else brings it (`dotnet nuget why`) |
| `src/Server.UI/Services/ImageProcessor.cs:17-50` | `ResizeAndCompressToJpegAsync`: `Image.LoadAsync` with the **default configuration**, so every format ImageSharp decodes (GIF, BMP, TIFF, TGA, PBM, QOI as well as JPEG, PNG, WebP). Then `Resize` with `ResizeMode.Crop` and `AnchorPositionMode.Center` to exactly the size asked, and `SaveAsJpegAsync` at the quality asked. No pixel limit. Metadata kept. Orientation not applied |
| `ImageProcessor.cs:57-62` | `IsImageFile`: judges by extension and admits .gif and .bmp. **No caller** |
| `src/Server.UI/Pages/Identity/Users/Components/ProfileInformationTab.razor:179`, `UserFormDialog.razor:182` | The two photo callers: the user's own profile and an administrator editing a user. Both pass 128, 128, 80 and read the upload with `OpenReadStream(GlobalVariables.MaxAllowedSize)`. Neither catches a refusal: an ImageSharp exception went into the circuit. `Accept=".jpg, .jpeg, .png, .webp"` (`:48`, `:35`) is only a browser filter |
| `src/Server.UI/Pages/Documents/Components/UploadFilesFormDialog.razor:141-160` | **A second use, not in ProjectTracking.** `Image.Load` (default configuration: every format), then a factor from the stored width (> 1600 px: 0.3; > 800: 0.5; else 0.9), `AutoOrient()`, `Resize(width × factor, 0)` and `Save(PngFormat.Instance)`. No pixel limit. Metadata kept. Every exception goes to a snackbar (`:164-167`) |
| `UploadFilesFormDialog.razor:1-3`, `Users/Components/_Imports.razor:65-69` | Eight ImageSharp `@using`s. Only the dialog's were used |
| `Directory.Build.props`, every `.csproj` | **No `NuGetAuditSuppress`.** The audit had warned since pass 48 (pass 48, Found but not fixed #2) |
| tests | **No image test** in any suite |

**Measured on the HEAD code** (`C:\gx47b\old`, a scratch project holding both code paths verbatim, run against
ImageSharp 3.1.12). The upload was a 300 × 200 JPEG, left half red and right half blue, carrying EXIF Orientation 6,
GPS latitude and a description.
- **Profile photo:**
  - **size and format:** 128 × 128 JPEG, quality 80, `YCbCrRatio420`;
  - **metadata kept:** markers `E0 E1 C0 C4 DB DA`, so the **APP1 EXIF with GPS reached the stored photo**;
  - **orientation not applied:** red stayed left;
  - **upscaling:** 40 × 30 became 128 × 128;
  - **other formats:** GIF, BMP and TIFF were accepted.
- **Document image:**
  - **PNG**, `Rgb` 8-bit for an opaque upload, `RgbWithAlpha` for a PNG with alpha;
  - **chunks:** `IHDR pHYs eXIf IDAT IEND`, so the **EXIF with GPS reached the stored PNG**;
  - **orientation applied.**
- **Document sizes:** the factor comes from the **stored** width, but `image.Width` inside the `Mutate` lambda is
  read after `AutoOrient`. It is applied to the **upright** width, `Convert.ToInt32` (half to even), and the height
  follows as `MathF.Round` (half to even):

  | Upload | Output |
  |---|---|
  | 300 × 200, Orientation 6 | 180 × 270 |
  | 1000 × 700 | 500 × 350 |
  | 2000 × 1500, Orientation 6 | 450 × 600 |
  | 1700 × 900, Orientation 6 | 270 × 510 |
  | 801 × 333 | 400 × 166 |
  | 900 × 335 | 450 × 168 |
  | 1000 × 701 | 500 × 350 |
  | 1000 × 699 | 500 × 350 |
  | 1601 × 1001 | 480 × 300 |
  | 500 × 3 | 450 × 3 |
  | 1 × 1 | 1 × 1 |

VERIFIED

### Differences from ProjectTracking's ImageProcessor (as of its pass 30)

| | ProjectTracking | Template before | Template now |
|---|---|---|---|
| Uses | photo only (Documents removed there) | photo **and** Documents | both, through one `Decode` |
| Formats | JPEG/PNG/WebP by content | any ImageSharp format | JPEG/PNG/WebP by content |
| Pixel limit | 50 MP from the header | none | 50 MP from the header |
| Metadata in output | none | **EXIF/GPS kept** in both | none |
| Orientation | applied (pass 30) | photo: not applied; Documents: applied | applied in both |
| Refusal in the UI | under the photo (pass 30) | error screen (photo); snackbar (Documents) | under the photo; snackbar (Documents) |
| `IsImageFile` | removed (pass 30) | present, unused | removed |
| Extra here | — | — | `ScaleToPngAsync` (`:80-102`), the `Decoded` record (`:105-108`) |

---

## Disproven premises

1. **"Following ProjectTracking's ImageProcessor" covers one use; the template has two.** The Documents dialog's
   resize-to-PNG is not in the model, since ProjectTracking removed Documents. Replacing it was simple: same decode
   and checks, then a scale and a PNG encode. So the pass did not stop. It is `ScaleToPngAsync`, which keeps
   ImageSharp's quirky sizing exactly (the table above). VERIFIED
2. **"Same output the template produces today" and S2 cannot both hold in full.** Today's photo carries the
   upload's EXIF (GPS included) and is not turned upright. Today's document PNG carries an `eXIf` chunk with the GPS.
   - **Kept:** size, format, quality and chroma subsampling.
   - **Changed, as S2 requires:** metadata (now none) and the photo's orientation (now applied).

   VERIFIED
3. **"Any ImageSharp suppressions": there were none.** The template never suppressed the ImageSharp advisories. It
   carried the NuGet audit warnings instead (336 audit lines in the HEAD build, ImageSharp among five packages).
   VERIFIED
4. **S6 found one path ProjectTracking does not have.** `System.Security.Cryptography.Pkcs` also comes in at
   **9.0.13** through `Microsoft.Data.SqlClient` 7.0.1, the SQL Server provider (EF Core SqlServer,
   `EntityFrameworkCore.Exceptions.SqlServer`, `Serilog.Sinks.MSSqlServer`). That is why Server.UI itself was
   flagged. The pin in `Domain.csproj` lifts that path to 10.0.14 too (`dotnet nuget why`: only v10.0.14 left).
   VERIFIED
5. **"Report counts against pass 47": the suites have moved on since pass 47.** Both baselines are given in
   Controls #3. VERIFIED
6. **"Code warnings unchanged from pass 47": the warning set has also moved since pass 47** (passes 48–57). Pass 47
   reported 19 warnings including NuGet audit lines, without listing them. The comparison is made against **HEAD**,
   built in a clean worktree. The 11 code warnings are identical line for line (Controls #1). VERIFIED
7. **Report location.** The brief says `GXTemplate-passes/`, where passes 19–52 live. Passes 54–57 are in
   `docs/passes/`. This report follows the brief. VERIFIED (read)

---

## The changes

### File map

| File | Change |
|---|---|
| `src/Server.UI/Services/ImageProcessor.cs` | Rewritten on SkiaSharp. `UnsupportedImageException` `:9`; the hardening in the class remarks `:11-22`; `MaxPixels` with its reason `:26-34`; `AllowedFormats` `:36-38`; `ResizeAndCompressToJpegAsync` `:48-71` (cover-and-crop `:53-64`, JPEG at the quality asked, 4:2:0, refused if the encoder returns nothing `:66-70`); **`ScaleToPngAsync` `:80-102`** (ImageSharp's factor and rounding `:85-87`, RGB when opaque `:89`, PNG `:99-101`); `Decoded` `:105-108`; `Decode` `:111-152` (format by content `:118-123`, the decoder's agreement `:125-131`, pixel limit `:133-139`, decode without colour space `:141-148`, upright `:150-151`); `Upright` `:158-181`; `FormatOf` `:184-192`. `IsImageFile` removed |
| `src/Server.UI/Server.UI.csproj:70-72` | `SixLabors.ImageSharp` 3.1.12 replaced by `SkiaSharp` 4.154.0, with a comment on the 64-bit pool |
| `src/Server.UI/Pages/Identity/Users/Components/_Imports.razor` | the five ImageSharp `@using`s removed (`:65-69` **(before)**) |
| `src/Server.UI/Pages/Identity/Users/Components/ProfileInformationTab.razor` | S3: the message under the photo `:57-61`; `_photoError` `:99-101`; the refusal caught and the upload stopped before anything is stored `:187-200` |
| `src/Server.UI/Pages/Identity/Users/Components/UserFormDialog.razor` | S3: the same in the administrator's dialog `:43-47`, `:182-184`, `:190-202` |
| `src/Server.UI/Pages/Documents/Components/UploadFilesFormDialog.razor` | the three ImageSharp `@using`s removed (`:1-3` **(before)**); the ImageSharp block (`:139-160` **(before)**) replaced by `ImageProcessor.ScaleToPngAsync` `:136-138`; the existing catch shows a refusal |
| `src/Domain/Domain.csproj:38-44` | S6: `System.Security.Cryptography.Pkcs` and `.Xml` pinned at 10.0.14, with when to remove |
| `tests/Application.IntegrationTests/Application.IntegrationTests.csproj:33-36`, `tests/Application.UnitTests/Application.UnitTests.csproj:23-26`, `tests/Server.UI.IntegrationTests/Server.UI.IntegrationTests.csproj:28-31` | S6: `Microsoft.AspNetCore.SignalR.Common` pinned at 10.0.14, with when to remove |
| `tests/Server.UI.IntegrationTests/ImageUploadHardeningTests.cs` (new) | From ProjectTracking (namespace rewritten), 12 photo cases, plus **13 document cases** written here |
| `tests/Server.UI.IntegrationTests/ProfilePhotoUploadComponentTests.cs` (new) | From ProjectTracking, 3 cases: a GIF (named .gif or .jpg) and a corrupt JPEG show their message under the photo; storage and profile untouched |
| `README.md:168-176` | S7: the 64-bit pool, and the Hosting Bundle 10.0.13 minimum |
| `tooling/smoke-generate.ps1` | The generated project references SkiaSharp 4.154.0 and has no ImageSharp reference or using; the two test files ship (`:518-528`); after the build, no `SixLabors.ImageSharp` in any `project.assets.json` and SkiaSharp restored (`:543-550`); the 28 image tests pass alone (`:598-605`); description `:34-37` |

### The tests

`tests/Server.UI.IntegrationTests/ImageUploadHardeningTests.cs`: every image is built in the test, from bytes where
what matters is what the file carries.

| Test | Shows |
|---|---|
| `TheThreeOfferedFormats_StillBecomeA128SquareJpeg_AtQuality80_420` `:89` (PNG, JPEG, WebP) | 128 × 128 JPEG; first luminance quantisation value 6 (quality 80); Y sampled 2 × 2 (4:2:0) |
| `ASmallImage_IsScaledUpTo128Square_AsBefore` `:108` | 40 × 30 becomes 128 × 128 |
| `AnyOtherFormat_IsRefusedFromItsContent_BeforeAnyDecoding` `:123` (GIF, BMP, TIFF) **X1** | each refused with "Only JPEG, PNG and WebP images can be uploaded."; the GIF and BMP are real, decodable files |
| `AnImageOverFiftyMegapixels_IsRefusedFromItsHeader` `:153` **X2** | a real 8 × 8 JPEG whose SOF0 claims 8000 × 7000 is refused, naming its size and the limit |
| `AJpegCarryingGpsExifAndAComment_LeavesNoMetadataInTheOutput` `:170` **X3** | the upload carries APP1 EXIF (Orientation, GPS IFD) and a COM segment; the output has no APP1–APP15 and no COM |
| `ExifOrientation_IsAppliedBeforeTheCrop` `:188` (6, 8, 3) **X4** | the stored left (red) is seen at the top, bottom or right; no APP1 in the output |
| `ADocumentImage_IsScaledAsBefore_ToAnRgbPng` `:251` (9 cases) | ImageSharp's measured sizes, PNG, colour type 2 (RGB) |
| `ADocumentPngWithTransparency_StaysRgba` `:266` | colour type 6, as ImageSharp's `RgbWithAlpha` |
| `ADocumentImageCarryingGpsExif_LeavesNoMetadataInThePng` `:278` | no `eXIf`, `tEXt`, `zTXt`, `iTXt`, `iCCP`, `tIME` or `pHYs`; no "Exif" or the comment's text |
| `ADocumentImage_IsTurnedUpright` `:292` | Orientation 6 puts red on top |
| `ADocumentImage_IsRefusedTheSameWay` `:303` | GIF and BMP refused; the 8000 × 7000 header refused |

`ProfilePhotoUploadComponentTests.cs`: `ARefusedUpload_ShowsItsMessage_UnderThePhoto_AndThePhotoStays` `:95`
(.gif and a GIF named .jpg) and `ACorruptJpeg_IsRefusedTheSameWay` `:109`.

**A test corrected before the run counted.** The first metadata test for the PNG allowed only `IHDR`, `IDAT` and
`IEND`. Skia also writes `sBIT` (significant bits per channel), which is encoder information, not metadata. The test
now forbids the metadata chunks by name. VERIFIED

### S6: the audit and the runtime

| Package | Version flagged | Advisories | How it comes in | Pin |
|---|---|---|---|---|
| `System.Security.Cryptography.Pkcs` | 10.0.11; 9.0.13 | GHSA-846f-f764-76mx (high), GHSA-2fmv-rv2c-59x7 (moderate) | DataProtection.EntityFrameworkCore 10.0.11; Microsoft.Data.SqlClient 7.0.1 (9.0.13) | 10.0.14, `Domain.csproj` |
| `System.Security.Cryptography.Xml` | 10.0.11 | GHSA-r955-8q5j-3h83, GHSA-c399-9853-6jmj, GHSA-7hqp-7c5m-4vq6 (high) | DataProtection.EntityFrameworkCore 10.0.11 | 10.0.14, `Domain.csproj` |
| `Microsoft.AspNetCore.SignalR.Common` | 10.0.11 | GHSA-334f-vvp4-7g6p (high) | Server.UI's SignalR.Client 10.0.11. Server.UI takes it from the shared framework (pruned); the three test projects that reference Server.UI do not | 10.0.14, those three test projects |

- **What the pins patch:** `Pkcs` is not in the shared framework, so it now ships in Server.UI's output at 10.0.14.
- **What they do not patch:** `Xml` and `SignalR.Common` run from the server's installed ASP.NET Core runtime. The
  publish is framework-dependent (`RuntimeIdentifier` win-x64, no `SelfContained`).
- **So the server needs runtime 10.0.13 or later: Hosting Bundle 10.0.13+, 10.0.14 current** (`README.md:173-176`).
- **This machine has 10.0.14** (`dotnet --list-runtimes`).

VERIFIED (audit, `nuget why`); INFERRED (the 10.0.13 fix version, from ProjectTracking pass 28)

**SkiaSharp on the server.**
- **What ships:** `SkiaSharp` brings `SkiaSharp.NativeAssets.Win32` and `.macOS` 4.154.0. A win-x64 publish carries
  `libSkiaSharp.dll` (x64) only.
- **The IIS pool must be 64-bit** (the default; "Enable 32-bit Applications" off).
- **Runtime libraries:** ProjectTracking pass 29 read the native library's imports. It needs no Visual C++ runtime.

INFERRED here (not re-run in this pass)

---

## Checks: the mutations

Built in the **Mutation** configuration (`dotnet build tests/Server.UI.IntegrationTests -c Mutation`). Each step:
1. backed up `ImageProcessor.cs`, applied one literal edit, and aborted unless it matched exactly once;
2. ran `dotnet build-server shutdown`, built, and ran `dotnet test -c Mutation --no-build --blame-hang-timeout 2m`
   on the two image fixtures (28 tests);
3. restored the bytes and compared SHA-256.

A control run first: 28/28 green. Every restore compared **True**. No test host aborted. The 18 Mutation folders were
deleted afterwards.

| # | Mutation (`ImageProcessor.cs`) | Named test | Result |
|---|---|---|---|
| X1 | GIF and BMP added to `AllowedFormats` (`:38`) | `AnyOtherFormat_IsRefusedFromItsContent_BeforeAnyDecoding` | **red** (gif, bmp) at `ImageUploadHardeningTests.cs:137`: "Expected … UnsupportedImageException to be thrown, but no exception was thrown." Also red: `ADocumentImage_IsRefusedTheSameWay` and both `ARefusedUpload_…` cases. 5 failed |
| X2 | the dimension check made `false && …` (`:135`) | `AnImageOverFiftyMegapixels_IsRefusedFromItsHeader` | **red** at `:164`: no exception. Also `ADocumentImage_IsRefusedTheSameWay`. 2 failed |
| X3 | the uploaded bytes returned instead of the re-encoded JPEG (`:70`) | `AJpegCarryingGpsExifAndAComment_LeavesNoMetadataInTheOutput` | **red** at `:179`: "…but found at least one item {0xE1}", the EXIF segment. 8 failed |
| X4 | `Upright(decoded, SKEncodedOrigin.TopLeft)` (`:151`) | `ExifOrientation_IsAppliedBeforeTheCrop` | **red**, all three cases, at `:201`: "Expected At(red).Red to be greater than 0xC8 because Orientation 6 puts red on the top, but found 0x40". Also red: the three rotated document sizes and `ADocumentImage_IsTurnedUpright`. 7 failed |

VERIFIED

---

## Controls

1. **Build.** After `dotnet build-server shutdown`, `dotnet build CleanArchitecture.Blazor.slnx --no-incremental`
   gave **0 errors**.
   - **HEAD baseline:** HEAD was built the same way in a clean worktree (`C:\gx47b\base`, removed afterwards). Its
     warning lines and these were normalised (paths, project suffix), and the code warnings are **identical, 11
     distinct**:
     - CS8600 ×2 and CS8603 ×2, `Application/Common/Extensions/DescriptionAttributeExtensions.cs:12,20,23,33`;
     - CS8601 ×2, `Application/Common/Mappings/MapsterConfiguration.cs:26,28`;
     - CS8603, `Server.UI/Components/Inputs/Select/TenantSelect.razor:13`;
     - CS8604, `Server.UI/Pages/Dashboard/Dashboard.razor:202`;
     - CS8602, `Server.UI/Pages/SystemManagement/AuditTrails.razor:107`;
     - MUD0002, `Server.UI/Components/Inputs/Select/MudDateTimeField.razor:1`;
     - NETSDK1206 (win7-x64/x86 runtime identifiers).
   - **NuGet audit:** **no audit warning** (HEAD: 336 lines over ImageSharp, Pkcs, Xml and SignalR.Common), with no
     `NuGetAuditSuppress` anywhere.
   - **`dotnet list package --vulnerable --include-transitive`:** "no vulnerable packages" for all 12 projects.

   VERIFIED
2. **Packages** (`dotnet list CleanArchitecture.Blazor.slnx package --include-transitive`):
   - **No `SixLabors.ImageSharp`** in any project.
   - **`SkiaSharp` 4.154.0:** Server.UI direct; transitive in Application.IntegrationTests, Application.UnitTests and
     Server.UI.IntegrationTests.
   - **`SkiaSharp.NativeAssets.Win32` and `.macOS` 4.154.0:** transitive.
   - **`SixLabors.Fonts` 1.0.0:** through **ClosedXML 0.105.1** (Infrastructure, Excel export), in 10 projects. It is
     not ImageSharp and carries no advisory.

   VERIFIED
3. **Every suite, on PostgreSQL** (`GX_TEST_PG` = the local 15.7 server at 127.0.0.1:5432), each run alone with
   `--no-build` on the final code:

   | Suite | Pass 47 | HEAD (pass 57) | Pass 47b | Change from HEAD |
   |---|---|---|---|---|
   | Application.UnitTests | 528 + 14 skipped | 568 + 14 skipped | **568 + 14 skipped** | none |
   | Infrastructure.UnitTests | 221 | 227 | **227** | none |
   | Application.IntegrationTests | 42 | 60 | **60** | none |
   | Server.UI.IntegrationTests | 261 | 283 | **311** | +28: `ImageUploadHardeningTests` 25, `ProfilePhotoUploadComponentTests` 3 |

   0 failed, no test-host abort. VERIFIED
4. **Smoke** (`powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1 -WorkRoot C:\gx47b\smoke`, with
   `GX_TEST_PG` set; the generated `appsettings.Development.json` is asserted to carry port 5434):

   **SMOKE PASSED (postgresql)**: 113 `ok`, 0 `FAIL`. The pass 47b lines:
   - `Server.UI.csproj references SkiaSharp 4.154.0`;
   - `no ImageSharp package reference or using (607 files)`;
   - `ImageUploadHardeningTests.cs is generated`, `ProfilePhotoUploadComponentTests.cs is generated`;
   - `0 errors`;
   - `no SixLabors.ImageSharp in any of 11 restore graphs`, `SkiaSharp 4.154.0 restored (4 projects)`;
   - **photo upload works:** `image uploads: Passed! - Failed: 0, Passed: 28`, run alone.

   **The generated suites** (GX_TEST_PG): 568 + 14 skipped · 206 · 60 · **311**. Without a server, each failed
   naming GX_TEST_PG, as designed. One test file's comment (`ProfilePhotoUploadComponentTests.cs:92`) was edited
   while the smoke ran, after its pack. It is a comment only. VERIFIED
5. **No migration.** No file under any `Migrations` folder changed; no `web.config` key added. VERIFIED

---

## Found but not fixed

1. **The output is not byte-identical to ImageSharp's.**
   - **What matches:** size, format, quality, chroma subsampling and PNG colour type.
   - **Resampling:** SkiaSharp is bilinear with mipmaps; ImageSharp was bicubic. Pixel values differ slightly.
   - **PNG chunks:** the PNG no longer has `pHYs` (resolution) and now has `sBIT`.

   VERIFIED
2. **The Documents dialog catches every exception into a snackbar** (`UploadFilesFormDialog.razor:143-146`,
   unchanged). A refusal is shown there, but so would be any other failure, with its raw message. Its
   `ReadFilesAsync` still reads each file into nothing (`// TODO`). Both predate this pass. VERIFIED (read)
3. **The administrator's user dialog has no component test of its own for S3.** Its change is the profile tab's,
   line for line. Rendering it needs the role manager and the whole user stack (as in ProjectTracking pass 30).
   VERIFIED (read)
4. **S5 was not rendered in a browser.** No old-code photo was stored and viewed through the app here. The serving
   path is unchanged and its tests are green. VERIFIED (read and run); the render was not done
5. **Each decode holds the upload twice** (the byte copy and `SKData.CreateCopy`), plus up to about 200 MB of
   pixels. This is the same as ProjectTracking. It is bounded by the 51 MB upload limit and 50 MP. INFERRED
6. **`SixLabors.Fonts` stays,** through ClosedXML (Controls #2). VERIFIED
7. **The template version (`GX.Blazor.Template.nuspec`) was not bumped.** That is outside this scope. VERIFIED (read)
