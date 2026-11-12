# Publie l'agent et l'interface pour linux-x64 (autonomes, sans .NET sur la cible), puis les installe par SSH.
# Le nom Ursa.mshome.net est fourni par le Default Switch d'Hyper-V et suit l'IP de la VM à chaque redémarrage.
param([string]$Target = 'ursanas@Ursa.mshome.net')
$ErrorActionPreference = 'Stop'
ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o LogLevel=ERROR $Target 'true'
if ($LASTEXITCODE) { throw "SSH vers $Target impossible" }
$root = Split-Path $PSScriptRoot
$out  = Join-Path $root 'artifacts'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# L'agent est un fichier unique ; l'interface reste un dossier (ressources MudBlazor et bibliothèque native SQLite).
dotnet publish "$root/src/Nas.Agent" -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o "$out/Nas.Agent" | Out-Null
if ($LASTEXITCODE) { throw 'publish Nas.Agent a échoué' }
dotnet publish "$root/src/Nas.Web" -c Release -r linux-x64 --self-contained -o "$out/Nas.Web" | Out-Null
if ($LASTEXITCODE) { throw 'publish Nas.Web a échoué' }
Copy-Item "$root/deploy/ursanas-agent.service", "$root/deploy/ursanas-web.service", "$root/deploy/install.sh" $out

$tar = Join-Path $out 'ursanas.tar'
tar -cf $tar -C $out Nas.Agent Nas.Web ursanas-agent.service ursanas-web.service install.sh
if ($LASTEXITCODE) { throw 'tar a échoué' }

scp -q $tar "${Target}:ursanas.tar"
if ($LASTEXITCODE) { throw 'scp a échoué' }

# Les avertissements de install.sh arrivent sur stderr : sous PowerShell 5.1 ils feraient échouer le script ; seul le code de sortie compte.
$ErrorActionPreference = 'Continue'
# Dossier d'extraction dans le répertoire personnel (rien n'est supprimé en dehors de lui).
ssh $Target 'D=$HOME/ursanas-deploy; mkdir -p $D; tar -mxf $HOME/ursanas.tar -C $D; sed -i "s/\r$//" $D/install.sh; sudo sh $D/install.sh $D'
if ($LASTEXITCODE) { throw "l'installation a échoué" }
