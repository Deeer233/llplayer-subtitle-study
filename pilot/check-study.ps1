#Requires -Version 7.6
param([string]$Video, [string]$Dictionary)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Join-Path (Split-Path $PSScriptRoot -Parent) 'upstream/LLPlayer'
$app = Join-Path $repo 'LLPlayer'
Add-Type -CompilerOptions '/nullable:enable' -Path @(
    (Join-Path $app 'Services/StudyDialogue.cs'),
    (Join-Path $app 'Services/StudyLocalLookup.cs'),
    (Join-Path $app 'Services/StudyDictionary.cs'),
    (Join-Path $app 'Services/StudyTranslationQueue.cs'),
    (Join-Path $app 'Services/StudySubtitleNotes.cs'),
    (Join-Path $app 'Services/StudyPlaybackProgress.cs'),
    (Join-Path $PSScriptRoot 'StudyLogicChecks.cs')
)
[StudyLogicChecks]::Run()
[StudyLogicChecks]::RunQueue()
$notes = Join-Path (Split-Path $PSScriptRoot -Parent) ('.pilot/subtitle-notes-check-' + [guid]::NewGuid().ToString('N'))
[StudyLogicChecks]::RunNotes($notes)
[StudyLogicChecks]::RunPlaybackProgress((Join-Path $notes 'progress'))

if ($Dictionary) {
    [StudyLogicChecks]::RunDictionary((Resolve-Path -LiteralPath $Dictionary).Path)
}

if ($Video) {
    $path = (Resolve-Path -LiteralPath $Video).Path
    $media = [uri]::new((Join-Path $PSScriptRoot 'media.mjs')).AbsoluteUri
    $json = & node --input-type=module -e @'
const [modulePath, path] = process.argv.slice(1);
const { normalizeSrt, parseSrt, probe } = await import(modulePath);
const { streams } = await probe(path);
const subtitle = (language, title) => streams.find(s => s.codec_type === 'subtitle'
    && language.test(s.tags?.language ?? '') && (!title || title.test(s.tags?.title ?? '')));
const english = subtitle(/^(eng|en(?:-[\w-]+)?)$/i);
const chinese = subtitle(/^(chi|zho|zh(?:-[\w-]+)?)$/i, /simplified|\u7b80\u4f53/i);
if (!english || !chinese) throw new Error('Expected tagged English and Simplified Chinese tracks');
const read = async (s) => parseSrt(await normalizeSrt(path, s.index));
console.log(JSON.stringify({ english: await read(english), chinese: await read(chinese) }));
'@ $media $path
    [StudyLogicChecks]::RunEpisode($json)
}

$xmlFiles = 'Controls/SubtitlesControl.xaml', 'Resources/PopupMenu.xaml', 'Views/SettingsDialog.xaml', 'Views/SubtitlesSidebar.xaml'
foreach ($path in $xmlFiles) {
    $markup = [xml](Get-Content -Raw -LiteralPath (Join-Path $app $path))
    if ($path -eq 'Controls/SubtitlesControl.xaml') {
        $code = Get-Content -Raw -LiteralPath (Join-Path $app "$path.cs")
        foreach ($handler in $markup.SelectNodes('//@Click | //@WordClicked | //@SizeChanged')) {
            if ($code -notmatch ('\bvoid\s+' + [regex]::Escape($handler.Value) + '\s*\(')) {
                throw "Missing event handler: $($handler.Value)"
            }
        }
    }
    Write-Output "PASS valid XML $path"
}
