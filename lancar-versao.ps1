<#
.SYNOPSIS
    Publica uma nova versão do PoPreyClip no GitHub. Quem já tem o app recebe o aviso de atualização.

.EXAMPLE
    .\lancar-versao.ps1 -Versao 1.0.2 -Notas "Corrige o microfone baixo"
#>
param(
    [Parameter(Mandatory = $true)][string]$Versao,
    [string]$Notas = ""
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

function Passo([string]$texto) { Write-Host "`n==> $texto" -ForegroundColor Cyan }
function Falhar([string]$texto) { Write-Host "`nERRO: $texto" -ForegroundColor Red; exit 1 }

if ($Versao -notmatch '^\d+\.\d+\.\d+$') { Falhar "Use o formato X.Y.Z, por exemplo 1.0.2." }
foreach ($ferramenta in 'git', 'dotnet', 'gh') {
    if (-not (Get-Command $ferramenta -ErrorAction SilentlyContinue)) { Falhar "$ferramenta não está instalado." }
}

Passo "Conferindo o repositório"
if ((git rev-parse --abbrev-ref HEAD) -ne 'main') { Falhar "Mude para o branch main antes de lançar (git checkout main)." }
if (git status --porcelain) { Falhar "Há alterações sem commit. Faça o commit antes de lançar a versão." }
$ultima = gh release view --json tagName --jq .tagName 2>$null
if ($ultima) {
    $atual = [Version]($ultima.TrimStart('v'))
    if ([Version]$Versao -le $atual) { Falhar "A versão $Versao precisa ser maior que a última publicada ($ultima)." }
    Write-Host "Última versão publicada: $ultima"
}

Passo "Rodando os testes"
dotnet test PoPreyClip.slnx -nologo -v q
if ($LASTEXITCODE -ne 0) { Falhar "Os testes falharam. Nada foi publicado." }

Passo "Atualizando o número da versão para $Versao"
$projeto = 'src\PoPreyClip.App\PoPreyClip.App.csproj'
$conteudo = Get-Content $projeto -Raw
$novo = $conteudo -replace '<Version>[^<]*</Version>', "<Version>$Versao</Version>"
if ($novo -ne $conteudo) {
    [IO.File]::WriteAllText((Resolve-Path $projeto), $novo)
    git add $projeto
    git commit -q -m "Release v$Versao"
}
git push -q origin main
if ($LASTEXITCODE -ne 0) { Falhar "Não foi possível enviar o código ao GitHub (git push)." }

Passo "Gerando o PoPreyClip.exe"
$saida = 'artifacts\release'
if (Test-Path $saida) { Remove-Item -Recurse -Force $saida }
dotnet publish src\PoPreyClip.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Versao -o $saida -nologo -v q
if ($LASTEXITCODE -ne 0) { Falhar "A compilação falhou." }
$exe = Join-Path $saida 'PoPreyClip.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Set-Content (Join-Path $saida 'PoPreyClip.exe.sha256') "$hash  PoPreyClip.exe" -Encoding ascii

Passo "Publicando a versão v$Versao no GitHub"
$arquivoNotas = Join-Path $saida 'notas.md'
$texto = if ($Notas) { $Notas } else { "Melhorias e correções." }
@"
## PoPreyClip $Versao

$texto

### Como atualizar

Quem já tem o PoPreyClip recebe um aviso dentro do app: é só clicar em **Atualizar agora**.

Para instalar do zero, baixe o **PoPreyClip.exe** abaixo, em **Assets**, e siga o [README](https://github.com/rildojunior/popreyclip#-instalação).
"@ | Set-Content $arquivoNotas -Encoding utf8
gh release create "v$Versao" $exe (Join-Path $saida 'PoPreyClip.exe.sha256') --title "PoPreyClip $Versao" --notes-file $arquivoNotas --target main
if ($LASTEXITCODE -ne 0) { Falhar "Não foi possível criar a versão no GitHub." }

Write-Host "`nPronto! Versão $Versao publicada:" -ForegroundColor Green
gh release view "v$Versao" --json url --jq .url
