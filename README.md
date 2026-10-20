# ZPL Print Manager

Serviço Windows em C#/.NET 10 para buscar arquivos ZPL em FTP/FTPS, encaminhá-los a impressoras TCP 9100 ou filas Windows RAW e arquivá-los após transmissão. A configuração é feita pelo aplicativo WinForms, com comunicação local autenticada.

**Transmissão aceita não comprova impressão física.** Falhas após possível envio são bloqueadas para resolução manual; falhas de arquivamento nunca autorizam reenvio automático.

## Estrutura

| Projeto | Responsabilidade |
|---|---|
| `src/ZplPrintManager.Core` | Estados, processamento, validação, limites e coordenação |
| `src/ZplPrintManager.Contracts` | Configurações, mensagens versionadas e enquadramento IPC |
| `src/ZplPrintManager.Infrastructure` | SQLite, FluentFTP, TCP, backup e administração |
| `src/ZplPrintManager.Infrastructure.Windows` | DPAPI, ACL/identidade do pipe e impressão RAW isolada |
| `src/ZplPrintManager.Service` | Windows Service, composição, polling, IPC e logs |
| `src/ZplPrintManager.Manager` | Interface WinForms em português |
| `tests` | Testes xUnit portáveis e específicos de Windows |
| `installer` | Inno Setup e ciclo de vida do serviço |

## Desenvolvimento no Ubuntu

Instale o SDK definido em `global.json` (**10.0.402**). O SDK não acompanha o repositório. As versões NuGet estão fixadas e os arquivos `packages.lock.json` são versionados.

```bash
dotnet restore ZplPrintManager.slnx --locked-mode
./scripts/test.sh
dotnet build ZplPrintManager.slnx -c Release --no-restore
```

Os testes portáveis usam SQLite real, servidor FTP local controlado e receptor TCP em loopback. Não precisam de impressora, credenciais ou acesso a servidores de produção. O runner de testes precisa criar sockets locais. WinForms e spooler não executam no Linux.

Para formatar: `dotnet format ZplPrintManager.slnx --no-restore`.

## Publicação Windows x64

No PowerShell, a partir da raiz:

```powershell
./scripts/publish.ps1 -Version 0.1.0
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer/ZplPrintManager.iss
```

O script publica Service e Manager separadamente, autocontidos, sem trimming ou ReadyToRun. O instalador requer Inno Setup **6.4.3**, instala uma conta virtual dedicada `NT SERVICE\ZplPrintManager`, configura ACLs e recuperação e mantém o processamento inicialmente pausado. Os arquivos de saída ficam em `artifacts/` e não são versionados.

O workflow `.github/workflows/build.yml` executa testes em Ubuntu, build/testes em Windows e gera binários e instalador de teste. Actions são fixadas por commit. **O workflow não assina nem promove releases automaticamente.** Assinatura requer certificado e uma etapa protegida de distribuição.

## Operação e validação

- [Manual de operação, permissões e recuperação](docs/OPERACAO.md)
- [Matriz de testes e homologação pendente](docs/HOMOLOGACAO.md)
- [Resultados da validação local](docs/VALIDACAO.md)
- [Decisões de implementação e limites](docs/ARQUITETURA.md)
- [Especificação técnica e funcional](ZPL-Print-Manager-Especificacao.md)

A compilação cruzada e os testes portáveis não substituem instalação, autorização, interface, impressão e testes de recuperação em Windows. Não há declaração de homologação física nem de impressão exatamente uma vez.
