# Homologação e evidências

## Automatizado no Ubuntu

Executar `scripts/test.sh`. A suíte cobre:

| Requisito | Evidência automatizada |
|---|---|
| T-01/T-02 | Chave de publicação única; IDs diferentes com conteúdo igual |
| T-04/T-05/T-06 | Extensão temporária, limites/vazio/incompleto, traversal e bytes binários |
| T-08/T-09/T-10/T-11 | Retry seguro limitado, incerteza após escrita, arquivamento sem reenvio, resposta de rename perdida |
| T-12/T-14/T-15 | Recuperação de envio, serialização por destino, snapshot preservado |
| T-16/T-17 | Autorização, idempotência, senha protegida por abstração, preservação de pendências |
| Backup/restore | SQLite consistente, hashes, preservação de identidades posteriores e bloqueio de pendências |
| Integração | SQLite real, socket TCP real, adaptador FluentFTP contra servidor controlado, FTPS sem downgrade |
| Coordenação | Drenagem de operações para manutenção e cancelamento sem perda de capacidade |

O teste Windows de DPAPI usa a identidade do runner. Isso não comprova DPAPI sob a conta virtual instalada. Os testes não representam uma bateria exaustiva de todos os pontos de falha da especificação.

## Windows: executar antes de distribuir

Preencher esta lista com versão/commit/hash do **mesmo pacote** gerado pela CI. Nenhum item abaixo está homologado apenas por compilação cruzada.

- [ ] Instalação limpa em Windows 11 x64 sem SDK, serviço com conta virtual e DPAPI após logout/reboot.
- [ ] UI em 100%, 150% e 200% DPI, assistente, validação de campos, cancelamento e indisponibilidade do serviço.
- [ ] Grupos Leitor/Operador/Administrador, token atualizado, cliente sem grupo e cliente de rede rejeitados.
- [ ] Repetição IPC após perda de resposta, tentativa com ID reutilizado e preservação de auditoria.
- [ ] FTP/FTPS real: TLS explícito/implícito, certificado vencido, hostname divergente, CA interna, senha incorreta e recuperação.
- [ ] Permissões de rename, colisões, Unicode, alteração durante download e servidor indisponível.
- [ ] RAW sob a identidade real do serviço, ID persistido, bytes parciais, spooler bloqueado, timeout/encerramento do auxiliar.
- [ ] Impressoras ZPL TCP e USB: modelo, firmware, driver, acentos, imagens e múltiplas etiquetas.
- [ ] Falta de papel, desconexão, reinício do spooler e distinção entre transmissão e impressão observada.
- [ ] T-03: republicação com mesmo ID e conteúdo divergente; nenhuma impressão automática adicional.
- [ ] T-07/T-12: interrupção forçada em cada fronteira de envio/commit/rename e nenhuma repetição incerta.
- [ ] T-13: disco cheio, SQLite sem escrita/corrompido e ausência de novos envios.
- [ ] Backup/restauração com fila ativa e backup antigo, DPAPI em outra máquina e reconciliação.
- [ ] Upgrade, falha de migração, rollback incompatível e desinstalação preservando dados.
- [ ] Teste contínuo de 72 horas: até 10 impressoras, 1.000 arquivos/dia, até 10 MiB por arquivo; medir memória/handles/disco/latência.
- [ ] Revisão de dependências/licenças/vulnerabilidades, assinatura dos executáveis e instalador, verificação de assinatura e hashes.

## Registro por versão

Registrar data, responsável, versão Windows, servidor FTP, certificados, modelos/firmwares/drivers, volume e tamanhos testados, resultados de cada item, evidências de falhas simuladas e limitações aceitas. A release fica bloqueada enquanto houver falha obrigatória ou homologação pendente.
