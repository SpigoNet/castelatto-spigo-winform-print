# Validação da implementação inicial

Ambiente: Ubuntu, SDK .NET 10.0.402. Data: 20/10/2026.

| Verificação executada | Resultado |
|---|---|
| Restore com arquivos de lock | Concluído |
| `dotnet format` | Concluído |
| xUnit Core em Release | 22 passaram, 0 falharam, 0 ignorados |
| xUnit Infrastructure em Release | 28 passaram, 0 falharam, 0 ignorados |
| Build Release da solução, incluindo projetos Windows | 0 erros, 0 avisos |
| Publish autocontido Service `win-x64` | Concluído |
| Publish autocontido Manager `win-x64` | Concluído |
| Auditoria NuGet incluindo dependências transitivas | Nenhuma vulnerabilidade reportada pela fonte consultada |

Os resultados TRX e a auditoria JSON estão em `artifacts/`, ignorado pelo Git. Os executáveis publicados estão em `artifacts/publish/service` e `artifacts/publish/manager`; distribua as pastas completas, não apenas os arquivos `.exe`.

Não executados neste ambiente: teste DPAPI em Windows, interface WinForms, serviço instalado, ACLs/impersonação Windows, spooler RAW, compilação/execução do instalador Inno Setup, workflow hospedado e homologação física/de carga. Esses itens permanecem na matriz `HOMOLOGACAO.md`. Os binários e o instalador não foram assinados; não representam uma release de produção aprovada.
