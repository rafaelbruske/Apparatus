# Extensão de exemplo do Apparatus, em PowerShell.
#
# Como funciona: o Apparatus inicia este script uma vez e conversa com ele por linhas de JSON (UTF-8),
# pela entrada e pela saída padrão. Cada linha é uma mensagem.
#
#   Apparatus -> extensão   {"type":"query","id":7,"query":"mundo","keyword":"hello"}
#   extensão -> Apparatus   {"type":"results","id":7,"results":[{"id":"a","title":"...","action":{"type":"copy","text":"..."}}]}
#
#   Apparatus -> extensão   {"type":"activate","id":8,"result":"count","data":{...}}   (quando a ação é "callback")
#   extensão -> Apparatus   {"type":"done","id":8}
#
#   extensão -> Apparatus   {"type":"notify","title":"...","message":"..."}   (aviso na bandeja, a qualquer momento)
#   extensão -> Apparatus   {"type":"log","message":"..."}                      (vai para o log do Apparatus)
#
# Ações de um resultado: open (target), copy (text), insert (text), reveal (path), callback (data), none.
# "alt" é a ação do Ctrl+Enter. Tudo que for escrito no stderr também vai para o log.
#
# ATENÇÃO: o Windows PowerShell 5.1 só lê acentos corretamente em scripts salvos como "UTF-8 com BOM".
# Este arquivo está assim; mantenha esse formato ao editar (no VS Code: "Salvar com codificação" > UTF-8 with BOM).

$utf8 = New-Object System.Text.UTF8Encoding($false)
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
$writer = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
$writer.AutoFlush = $true
$writer.NewLine = "`n"

function Send($message) {
    $writer.WriteLine(($message | ConvertTo-Json -Compress -Depth 8))
}

while ($null -ne ($line = $reader.ReadLine())) {
    if ($line.Trim().Length -eq 0) { continue }
    try { $request = $line | ConvertFrom-Json } catch { continue }

    if ($request.type -eq 'query') {
        $text = [string]$request.query
        $results = @()

        if ($text.Length -eq 0) {
            $results += @{ id = 'help'; title = 'Digite algo depois de "hello"'; subtitle = 'Por exemplo: hello mundo'; action = @{ type = 'none' } }
        }
        else {
            $now = Get-Date -Format 'HH:mm:ss'
            $results += @{ id = 'greet'; title = "Olá, $text!"; subtitle = 'Enter copia a saudação'; score = 20; action = @{ type = 'copy'; text = "Olá, $text!" } }
            $results += @{ id = 'time'; title = "Agora são $now"; subtitle = 'Enter digita a hora no aplicativo em uso'; score = 10; action = @{ type = 'insert'; text = $now } }
            $results += @{ id = 'temp'; title = 'Abrir a pasta temporária'; subtitle = $env:TEMP; action = @{ type = 'open'; target = $env:TEMP }; alt = @{ type = 'copy'; text = $env:TEMP } }
            $results += @{ id = 'count'; title = 'Contar os arquivos da pasta Downloads'; subtitle = 'O script faz a conta e avisa na bandeja'; action = @{ type = 'callback'; data = @{ folder = 'Downloads' } } }
        }

        Send @{ type = 'results'; id = $request.id; results = $results }
    }
    elseif ($request.type -eq 'activate') {
        if ($request.result -eq 'count') {
            $folder = Join-Path $env:USERPROFILE ([string]$request.data.folder)
            $count = 0
            if (Test-Path $folder) { $count = (Get-ChildItem $folder -File -ErrorAction SilentlyContinue | Measure-Object).Count }
            Send @{ type = 'notify'; title = 'Olá, PowerShell'; message = "A pasta tem $count arquivo(s)." }
        }
        Send @{ type = 'done'; id = $request.id }
    }
}
