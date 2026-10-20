# Arquitetura e decisões

## Fluxo e persistência

`ProcessingEngine` descobre publicações, persiste aquisição, reconcilia rename, baixa em binário para `.part`, verifica tamanho/metadados/hash, faz flush durável e promove para `.zpl`. Antes de chamar o transporte, grava tentativa e estado `Sending`. Reinício nessa fronteira produz `Uncertain`, jamais um reset geral da fila.

SQLite contém `Jobs`, `ProcessedIdentities`, `Attempts`, `RemoteOperations`, `AuditEvents`, `Commands`, `Config` e `SchemaVersions`. Configurações tipadas ficam em JSON versionado dentro de `Config`; trabalhos têm snapshots JSON, colunas de estado/próxima tentativa e índices relacionais. A chave de publicação é única por conexão/mapeamento/nome; hash não representa intenção de impressão. A migração inicial é transacional e verifica `quick_check`. Acesso ao banco é serializado e transações não abrangem rede/spooler.

Um arquivo exclusivo de processo impede dois hosts locais. Há uma fila serial por identificador físico e até dois destinos em paralelo. Uma barreira de manutenção impede admissão e drena operações para configurações, backup e restauração. Pausa pode ser registrada durante drenagem e é conferida antes de novo envio. A descoberta é sequencial por conexão; optou-se por um download por destino dentro do limite global de dois, simplificando backpressure e ordenação.

## Transportes e segurança

FluentFTP usa FTPS explícito como padrão, criptografia do canal de dados e validação normal de certificados. Não há fallback para FTP. Credenciais não são retornadas à interface nem serializadas em configuração. DPAPI usa o contexto da conta virtual do serviço e ACLs restritas.

TCP contabiliza escritas parciais. A partir da primeira chamada de escrita, falhas são conservadoramente incertas. RAW executa no próprio executável em modo auxiliar, com argumentos sem credenciais, entrada privada e prazo controlado. Após `StartDocPrinter`, o auxiliar aguarda confirmação do serviço de que o ID foi persistido antes de escrever bytes. Encerrar o auxiliar não cancela impressão já entregue.

O pipe local tem ACL por grupos e negação de tokens de rede. O servidor lê uma mensagem limitada, impersona apenas para identificar o cliente e encerra a impersonação antes de operar como serviço. Cada comando verifica papel, versão e correlação. Mutações registram a resposta por ID de idempotência; respostas perdidas podem ser recuperadas sem criar outra reimpressão.

## Limites deliberados e validação externa

- Não existe garantia física de impressão exatamente uma vez. Não há adaptador específico de telemetria ZPL nem monitoramento contínuo via `GetJob`; a evidência automática é de transporte. A intervenção RAW consulta a fila existente no Windows.
- O histórico visual/CSV limita a consulta aos mil trabalhos mais recentes. Registros de identidade nunca são expurgados; a retenção de detalhes elimina tentativas encerradas, mantendo o registro mínimo do trabalho e auditoria.
- Renomeação FTP requer consumidor único e produtores confiáveis. Checagem prévia de destino não é uma operação distribuída atômica contra outro cliente externo.
- Mudanças de host/usuário não alteram snapshots de trabalhos existentes. Corrija o acesso ao destino original ou encerre explicitamente pendências antes de migrar.
- A primeira interface usa formulários gerados a partir dos contratos, com rótulos em português e valores técnicos de enumeração. Validação visual/DPI exige Windows.
- A versão inicial usa Inno Setup, conforme seção 19 da especificação. Assinatura, publicação de release, homologação em VM/hardware e validação de carga são etapas externas; não são simuladas como concluídas.
