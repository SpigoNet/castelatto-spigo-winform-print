# ZPL Print Manager

## Especificação técnica e funcional

**Versão:** 1.0 — proposta para implementação  
**Data:** 20 de outubro de 2026  
**Idioma:** Português brasileiro  
**Plataforma:** Windows, C#/.NET 10, Windows Service e WinForms

## 1. Objetivo e contexto

O ZPL Print Manager deve monitorar pastas em um servidor FTP/FTPS, baixar arquivos ZPL completos, encaminhá-los à impressora associada à pasta e arquivar os arquivos processados em `/impressos`. O processamento deve funcionar sem usuário conectado ao Windows. Toda configuração operacional deve ser feita em um aplicativo visual, sem edição manual de JSON.

Esta especificação consolida os requisitos fornecidos na conversa “Estrutura sistema impressão” e no pedido de continuação. O histórico recuperado contém as solicitações do usuário, mas não disponibiliza o texto das respostas anteriores. Os detalhes não expressos nesses requisitos são decisões propostas neste documento, e não transcrições de decisões anteriores.

**Princípio fundamental:** concluir o envio de bytes não comprova que uma etiqueta saiu fisicamente. O sistema deve representar separadamente processamento, transmissão, arquivamento e evidências de impressão.

### 1.1 Escopo da primeira versão

- Serviço Windows com inicialização automática e processamento persistente.
- Interface WinForms para configuração, diagnóstico, acompanhamento e intervenção.
- Um ou mais servidores FTP/FTPS; mapeamentos entre pastas remotas e impressoras.
- Impressão ZPL por TCP, porta padrão 9100, ou fila Windows com dados RAW, inclusive impressoras USB.
- SQLite local para configuração, histórico, tentativas e auditoria.
- Polling, download binário seguro, recuperação após reinício e arquivamento remoto.
- Controle de duplicidade, tratamento conservador de resultados incertos e reimpressão manual auditada.
- Instalador, atualização, backup, desinstalação e testes automatizados e físicos.

### 1.2 Fora do escopo inicial

Editor ou renderizador de etiquetas, conversão de PDF/imagens para ZPL, SFTP, aplicação web, configuração remota pela internet, alta disponibilidade ativa-ativa e garantia universal de impressão física exatamente uma vez. FTPS e SFTP são protocolos diferentes; selecionar FTPS não fornece suporte a SFTP.

## 2. Premissas e limites

1. As impressoras devem aceitar ZPL ou uma emulação homologada. Uma conexão USB ou uma fila RAW não transforma uma impressora comum em impressora ZPL.
2. Um arquivo pode conter várias etiquetas e comandos de quantidade. O sistema deve enviar o arquivo uma vez por trabalho, sem presumir “um arquivo = uma etiqueta”.
3. Cada pasta de entrada deve ter um único consumidor autorizado. Não suportar dois serviços independentes consumindo a mesma pasta na primeira versão.
4. O produtor deve usar nomes únicos e publicar arquivos completos, preferencialmente por renomeação de um nome temporário para `.zpl`.
5. O servidor deve permitir listar, ler e renomear arquivos e acessar as pastas de destino. A criação dessas pastas pode ser feita pelo aplicativo ou previamente por um administrador.
6. O computador deve permanecer ligado e sem suspensão durante a operação. Impressoras, servidor e rede devem estar acessíveis pela conta do serviço.
7. A garantia de processamento depende da preservação do banco e do spool local. Restaurar um backup antigo exige reconciliação antes de retomar envios.

## 3. Arquitetura e tecnologias

```text
Produtor de etiquetas
        |
        v
FTP/FTPS: pastas por impressora
        |
        v
ZPL Print Manager Service (.NET 10)
  descoberta -> download -> fila persistente -> envio -> arquivamento
        |              |                       |
        |              v                       +-> TCP 9100 -> impressora ZPL
        |         SQLite + spool local         +-> spooler RAW -> USB/ZPL
        |
  Named Pipe local autenticado
        |
WinForms: configuração, status, histórico e ações
```

| Camada | Decisão proposta |
|---|---|
| Linguagem/runtime | C# e .NET 10; projetos Windows em `net10.0-windows` |
| Serviço | Worker Service, `BackgroundService`, Generic Host e `Microsoft.Extensions.Hosting.WindowsServices` |
| Interface | WinForms com operações assíncronas e cancelamento |
| FTP/FTPS | FluentFTP, versão estável compatível homologada e fixada no build |
| TCP | `System.Net.Sockets`, envio de bytes com timeout e contabilização |
| RAW | Interoperabilidade Win32 com `winspool.drv` |
| Dados | SQLite por `Microsoft.Data.Sqlite`, migrações versionadas |
| Comunicação local | Named Pipes com ACL e validação da identidade Windows |
| Logs | `Microsoft.Extensions.Logging`, provedor de arquivo com rotação e Windows Event Log |
| Distribuição | Instalador MSI, proposto com WiX, publicação autocontida `win-x64` |
| Testes | xUnit, adaptadores simulados e integração em Windows |

A base de Windows Service segue o modelo documentado pela Microsoft em [Create Windows Service using BackgroundService](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service). A biblioteca escolhida oferece FTP/FTPS e APIs assíncronas: [FluentFTP](https://github.com/robinrodricks/FluentFTP).

### 3.1 Separação de responsabilidades

- **Service:** único proprietário da escrita no banco, credenciais, arquivos locais e estado das filas.
- **Desktop:** apresenta dados e envia comandos; não abre diretamente o banco nem imprime trabalhos de produção por conta própria.
- **Core:** regras de negócio e máquina de estados, sem dependência da interface.
- **Infrastructure:** implementações FTP, SQLite, impressão e armazenamento.
- **Contracts:** mensagens versionadas do canal local.
- **Tests/Installer:** validação e empacotamento.

O serviço não deve abrir janelas nem depender da sessão interativa. Fechar o WinForms não interrompe o processamento. Se o serviço estiver indisponível, a interface deve explicar a situação e oferecer início do serviço quando o usuário tiver permissão.

## 4. Requisitos funcionais

| ID | Requisito |
|---|---|
| RF-01 | Cadastrar, editar, testar e desativar conexões FTP/FTPS visualmente. |
| RF-02 | Cadastrar destinos TCP e filas Windows RAW. |
| RF-03 | Associar cada pasta de entrada a uma impressora; impedir mapeamentos conflitantes. |
| RF-04 | Detectar arquivos por polling, sem depender de notificações do servidor. |
| RF-05 | Baixar e validar arquivos antes de qualquer envio. |
| RF-06 | Persistir trabalhos e tentativas e recuperar filas após reinício. |
| RF-07 | Serializar os envios por impressora física e permitir paralelismo entre impressoras. |
| RF-08 | Arquivar após transmissão aceita, sem declarar automaticamente impressão física. |
| RF-09 | Repetir falhas transitórias somente quando o reenvio for seguro. |
| RF-10 | Bloquear reenvio automático em situações de resultado incerto. |
| RF-11 | Exibir histórico filtrável, eventos, erros e evidências de status. |
| RF-12 | Pausar/retomar descoberta e envio por mapeamento, impressora ou globalmente. |
| RF-13 | Permitir reimpressão, descarte e resolução manual com motivo e auditoria. |
| RF-14 | Realizar backup e restauração controlada e exportar diagnóstico sem segredos. |

### 4.1 Interface visual

**Assistente inicial:** verificar serviço e permissões; cadastrar conexão; testar leitura e renomeação em arquivo de teste exclusivo; cadastrar impressora; escolher pasta; validar arquivamento; oferecer envio explícito de etiqueta de teste; ativar mapeamento.

**Painel:** estado do serviço, último heartbeat, última consulta bem-sucedida ao FTP, quantidade de trabalhos por estado, filas pausadas, espaço livre e alertas. Separar indicadores “transmitidos”, “aguardando análise” e “impressão confirmada”.

**Conexões:** nome, host, porta, protocolo, usuário, senha mascarada, modo passivo, timeouts e teste detalhado. Nunca devolver a senha armazenada à interface. Campo vazio em uma edição significa preservar a senha, com ação separada para substituí-la.

**Impressoras:** nome amigável, tipo, endereço/porta ou nome exato da fila Windows, estado observado e horário da consulta. O teste de conectividade não deve imprimir. O botão de etiqueta de teste deve informar que consumirá mídia e gerar um trabalho auditado.

**Mapeamentos:** conexão, pasta de entrada, impressora, extensão, polling, protocolo de publicação, destinos de processamento/arquivo/erro e estado ativo. Pré-visualizar os caminhos resultantes antes de salvar.

**Histórico:** filtros por período, arquivo, identificador, impressora e estado; detalhe de tentativas, hashes, tamanho, tempos, bytes, erros e movimentações. Exportação CSV em UTF-8, neutralizando fórmulas iniciadas por `=`, `+`, `-` ou `@`.

**Administração:** versões, retenção, backup, restauração, logs e serviço. Reiniciar/parar o serviço exige direitos Windows apropriados; a interface comum não deve permanecer elevada.

### 4.2 Papéis locais

| Papel | Permissões |
|---|---|
| Leitor | Consultar painel e histórico sem conteúdo sensível. |
| Operador | Leitor + pausar, retomar e resolver trabalhos, incluindo reimpressão auditada. |
| Administrador | Operador + configurações, credenciais, restauração e administração do serviço. |

Usar grupos locais Windows criados/configurados pelo instalador. O serviço deve verificar autorização em cada comando; esconder botões não constitui controle de acesso.

## 5. Configurações e valores iniciais

Os valores abaixo são padrões propostos, ajustáveis pela interface e sujeitos à homologação.

| Parâmetro | Padrão |
|---|---|
| Protocolo | FTPS explícito, sem fallback automático para FTP |
| Porta FTP/FTPS explícito | 21 |
| FTPS implícito | Opcional, porta inicial 990 |
| Modo de dados | Passivo |
| Polling | 5 segundos por conexão; mínimo configurável de 1 segundo |
| Estabilidade alternativa | 3 observações iguais cobrindo ao menos 10 segundos |
| Extensões aceitas | `.zpl`, comparação de extensão sem diferenciar maiúsculas |
| Tamanho máximo | 10 MiB por arquivo; rejeitar arquivo vazio |
| Timeout de conexão | 10 segundos |
| Timeout de inatividade de transferência | 30 segundos |
| Prazo total de download/envio | 120 segundos por operação, ajustável |
| Concorrência de download | 2 globalmente |
| Concorrência de impressão | 1 por destino físico; até 2 destinos simultâneos |
| Tentativas automáticas seguras | 5 no total, incluindo a primeira |
| Esperas entre tentativas | 5, 15, 60 e 300 segundos, com jitter de até 20% |
| Limiar de disco livre | Suspender ingestão abaixo de 1 GiB ou reserva calculada para downloads ativos |
| Histórico detalhado | 180 dias |
| Payload local de trabalhos encerrados | 7 dias |
| Identidades antirrepetição | Sem expurgo automático na primeira versão |
| Arquivo remoto | Sem exclusão automática na primeira versão |

FTPS deve validar cadeia, nome e validade do certificado e proteger canais de controle e dados. Certificados internos devem ser confiados pela administração do Windows; não oferecer “aceitar qualquer certificado”. FTP sem TLS requer escolha explícita e aviso de credenciais/conteúdo sem criptografia. As opções de conexão são descritas em [FluentFTP — FTP Connection](https://github.com/robinrodricks/FluentFTP/wiki/FTP-Connection).

Alterações geram uma revisão de configuração. Trabalhos já adquiridos preservam destino e revisão originais. Trocar a impressora de um trabalho pendente exige ação explícita auditada; nunca redirecionar silenciosamente uma fila existente.

## 6. Organização remota e contrato com o produtor

Exemplo de uma conexão com duas impressoras:

```text
/entrada/expedicao/<id-unico>.zpl
/entrada/recebimento/<id-unico>.zpl
/processando/<mapeamento-id>/<trabalho-id>.zpl
/impressos/<mapeamento-id>/2026/10/20/<trabalho-id>.zpl
/erros/<mapeamento-id>/<trabalho-id>.zpl
```

Os caminhos são relativos à raiz visível pela conta FTP e configuráveis. Não presumir acesso à raiz física do servidor. O nome original permanece no histórico. As pastas de processamento, arquivo e erro não podem estar sob varredura de entrada nem sobrepor outro mapeamento. A primeira versão não deve fazer varredura recursiva.

### 6.1 Publicação recomendada

1. O produtor gera um identificador único por solicitação, inclusive para impressões de conteúdo igual.
2. Envia `<id>.uploading` em modo binário.
3. Fecha a transferência e renomeia para `<id>.zpl` no mesmo servidor.
4. Não modifica nem reutiliza esse identificador após a publicação.

O serviço ignora extensões temporárias. A semântica de renomeação deve ser homologada no servidor; o FTP prevê `RNFR/RNTO`, mas isso não torna operações no FTP e no SQLite uma única transação. Referência: [RFC 959](https://www.rfc-editor.org/rfc/rfc959).

### 6.2 Modo de compatibilidade

Se o produtor não puder publicar por renomeação, exigir tamanho e data de modificação estáveis nas observações configuradas, validar metadados antes e depois do download e descartar a cópia parcial se houver alteração. Essa heurística não prova que uma gravação terminou: um upload pode ficar parado. O assistente deve identificar esse modo como garantia reduzida; operações que exijam integridade estrita devem usar publicação por renomeação ou marcador de conclusão acordado.

## 7. Fluxo de processamento

### 7.1 Descoberta e aquisição

1. Executar polling com `PeriodicTimer` ou mecanismo equivalente, sem sobreposição de ciclos da mesma conexão.
2. Listar preferencialmente com metadados estruturados disponíveis no servidor. Não distinguir arquivos somente por horário local.
3. Ignorar diretórios, temporários, caminhos fora do mapeamento e identidades já conhecidas.
4. Registrar o candidato e sua identidade em transação SQLite.
5. Confirmar elegibilidade pelo contrato de publicação ou estabilidade.
6. Persistir a intenção de aquisição e o caminho remoto exclusivo de processamento.
7. Renomear a origem para `/processando/.../<trabalho-id>.zpl`.
8. Persistir aquisição concluída. Se a resposta se perder, reconciliar origem e destino antes de tentar novamente.

Não considerar o rename um lock distribuído universal. O produto exige consumidor único e nomes exclusivos. Se rename não for suportado, bloquear ativação do mapeamento na primeira versão, em vez de introduzir uma alternativa menos segura silenciosamente.

### 7.2 Download seguro

- Usar modo binário e streaming para `<trabalho-id>.part` em diretório privado local.
- Gerar nomes locais internamente; nunca concatenar caminhos locais com nomes remotos não confiáveis.
- Aplicar limites de tamanho e tempo durante a leitura, não apenas antes dela.
- Calcular SHA-256 dos bytes; conferir tamanho e, quando disponível, checksum remoto compatível.
- Consultar novamente os metadados para detectar alterações durante a transferência.
- Fazer validação estrutural mínima de ZPL, configurável por perfil homologado. A presença de `^XA` e `^XZ` é uma heurística, não um parser completo nem prova de segurança.
- Preservar bytes, codificação e quebras de linha; não adicionar BOM ou converter o conteúdo para texto e de volta.
- Descarregar buffers para disco, fechar o arquivo e renomear localmente para `.zpl` no mesmo volume.
- Somente então persistir o estado `PRONTO` com hash, tamanho e caminho.

Arquivos inválidos ficam em quarentena; nenhum byte deve ser enviado. Arquivos `.part` abandonados podem ser descartados e baixados novamente quando não houver tentativa de transmissão. Um arquivo final órfão deve ser reconciliado pelo identificador antes de qualquer descarte.

### 7.3 Fila e ordem

Persistir a fila no SQLite; estruturas em memória servem apenas para sinalização. Ordenar por sequência local de descoberta e identificador como desempate. Isso não garante a ordem de criação no servidor; sequenciamento de negócio exige informação do produtor.

Todos os mapeamentos destinados à mesma impressora devem compartilhar a mesma fila serial. Alertar sobre cadastros duplicados do mesmo IP/porta ou fila Windows; um equipamento cadastrado por TCP e USB precisa de associação explícita ao mesmo destino físico.

Por padrão, resultado incerto ou erro de impressão bloqueia o avanço daquela impressora até resolução do operador. Outras impressoras continuam. Falha apenas no arquivamento não bloqueia trabalhos seguintes, desde que exista registro durável da transmissão e da identidade consumida.

### 7.4 Envio

Antes da chamada com possível efeito externo, persistir uma tentativa em `ENVIANDO`, com destino, hash e horário. Não manter transação SQLite aberta durante operações de rede ou spooler.

Após sucesso, persistir `TRANSMITIDO` e a evidência do transporte. Se o processo cair após enviar e antes de persistir, o resultado será incerto na recuperação. Essa janela não pode ser eliminada com uma transação local.

### 7.5 Arquivamento

O padrão é arquivar após transmissão aceita pelo transporte, e não após prova de saída física. O nome `/impressos` é uma convenção operacional; a interface deve exibir “Arquivado após transmissão — impressão física não confirmada” quando aplicável.

Persistir a intenção e o caminho de destino antes do rename remoto. Não sobrescrever arquivos existentes. Destinos exclusivos usam o identificador do trabalho, com caminho e data fixados na intenção para que a repetição seja idempotente.

Se a movimentação falhar após transmissão, marcar `ARQUIVAMENTO_PENDENTE` e repetir apenas a movimentação. Nunca retornar o trabalho à fila de envio. Se a resposta do rename se perder, verificar os caminhos de origem e destino; se o destino corresponder ao trabalho e a origem tiver desaparecido, concluir o arquivamento. Se ambos existirem, nenhum existir ou houver identidade divergente, solicitar análise.

Falha de movimentação para `/erros` também não autoriza impressão. Manter cópia local e registro do caminho remoto conhecido.

## 8. Estados e semântica de sucesso

### 8.1 Estado do processamento

| Estado | Significado e próxima ação |
|---|---|
| `DESCOBERTO` | Candidato registrado; verificar publicação. |
| `AGUARDANDO_ESTABILIDADE` | Ainda não elegível para aquisição. |
| `AQUISICAO_PENDENTE` | Intenção persistida; executar/reconciliar rename remoto. |
| `ADQUIRIDO` | Arquivo reservado no caminho exclusivo. |
| `BAIXANDO` | Transferência local em andamento. |
| `PRONTO` | Payload íntegro e durável; aguarda sua vez. |
| `ENVIANDO` | Pode haver efeito externo; recuperação exige cautela. |
| `TRANSMITIDO` | Transporte aceitou o trabalho conforme critérios abaixo. |
| `ARQUIVAMENTO_PENDENTE` | Envio concluído; falta movimentação remota. |
| `ARQUIVADO` | Movimentação remota reconciliada. |
| `AGUARDANDO_RETRY` | Falha seguramente anterior a efeito externo; repetir etapa registrada. |
| `RESULTADO_INCERTO` | Pode ter havido impressão parcial ou completa; intervenção. |
| `QUARENTENA` | Conteúdo ou condição permanente inválida. |
| `DESCARTADO` | Encerramento manual sem envio adicional, com motivo. |

O banco deve preservar a etapa de retorno e o contador por etapa. `ARQUIVADO` é terminal para o fluxo automático, mas não constitui comprovação física.

### 8.2 Evidência de impressão, independente do estado acima

| Evidência | Interpretação |
|---|---|
| `NAO_DISPONIVEL` | Nenhuma informação física confiável. |
| `TRANSPORTE_ACEITOU` | TCP concluiu escrita ou spooler aceitou o job. |
| `FILA_REPORTOU_CONCLUSAO` | Windows reportou conclusão; não garante saída física. |
| `DISPOSITIVO_REPORTOU_CONCLUSAO` | Integração homologada correlacionou o trabalho e a conclusão no dispositivo. |
| `CONFIRMADO_PELO_OPERADOR` | Pessoa confirmou, com identidade e horário. |

Estado geral da impressora (“pronta”, “sem papel”, “tampa aberta”) não confirma um trabalho específico. Respostas ZPL de status, quando suportadas pelo modelo, devem ser implementadas como adaptador opcional homologado. Sem correlação inequívoca, mostrar apenas telemetria do equipamento. Não abrir leituras concorrentes no socket de impressão sem protocolo de enquadramento definido.

## 9. Adaptadores de impressão

### 9.1 TCP 9100

Abrir conexão por trabalho, enviar exatamente o payload e fechar a conexão ao final. Controlar escritas parciais, cancelamento e prazo total. Concluir a escrita significa aceitação pela pilha de transporte, não processamento físico pela impressora.

Falha de conexão comprovadamente anterior à primeira escrita permite retry. Exceção após iniciar uma escrita, mesmo sem contagem final de bytes disponível, deve resultar em `RESULTADO_INCERTO`. Não reiniciar o arquivo do começo automaticamente: parte das etiquetas pode ter sido impressa.

A porta é configurável. Restringir o acesso à rede de impressão; o canal TCP 9100 não deve ser exposto publicamente.

### 9.2 Spooler Windows RAW e USB

Usar `OpenPrinter`, `StartDocPrinter` com tipo `RAW`, `StartPagePrinter`, `WritePrinter`, `EndPagePrinter`, `EndDocPrinter` e fechamento seguro dos handles. Verificar retornos e bytes escritos em cada operação; persistir o identificador retornado por `StartDocPrinter` assim que disponível.

Não usar `PrintDocument`/GDI para renderizar ZPL, nem comandos de shell para copiar arquivos a portas. Os bytes devem chegar à impressora sem conversão gráfica. A fila, o processador e o driver precisam ser homologados para RAW. Referências: [envio direto de dados](https://learn.microsoft.com/en-us/windows/win32/printdocs/sending-data-directly-to-a-printer) e [tipo RAW](https://learn.microsoft.com/windows-hardware/drivers/print/raw-data-type).

Sucesso exige a finalização aceita pelo spooler; acompanhar o job quando possível com [GetJob](https://learn.microsoft.com/en-us/windows/win32/printdocs/getjob). Desaparecimento do job não é prova de impressão: ele pode ter sido excluído ou removido pelo sistema.

Após criação do job, falhas exigem reconciliação antes de nova submissão. Cancelar no spooler é uma ação de melhor esforço e não desfaz etiquetas já emitidas. Identificar jobs por fila, ID, documento com UUID e horário; IDs isolados podem ser reutilizados.

APIs do spooler podem bloquear. Executá-las fora da thread da interface e do polling. A implementação deve usar um processo auxiliar limitado para chamadas potencialmente bloqueantes; ao expirar o prazo, encerrar o auxiliar, marcar resultado incerto e pausar o destino. Encerrar o auxiliar não cancela necessariamente o job já entregue.

Instalar a fila para uso da máquina e garantir acesso à identidade do serviço. Uma impressora visível apenas no perfil do usuário interativo não é suficiente. Os testes devem ser executados pelo serviço, sob sua própria identidade.

## 10. Duplicidade e reimpressão

### 10.1 Identidade

Usar UUID interno para cada trabalho e chave única `(conexao_id, mapeamento_id, id_publicacao)`, quando o produtor fornecer ID. No modo recomendado, o nome único do arquivo representa esse ID. A comparação de caminhos deve respeitar a semântica do servidor; não converter todos os caminhos para minúsculas.

Sem ID confiável, usar uma assinatura composta de caminho, tamanho, modificação remota e hash após download. Essa alternativa não distingue perfeitamente uma republicação intencional idêntica e deve ser documentada como limitada. Reuso detectado com conteúdo divergente gera conflito e quarentena.

SHA-256 identifica conteúdo, não intenção de impressão. Dois IDs diferentes com o mesmo hash podem ser duas solicitações legítimas; não suprimir automaticamente somente por hash.

### 10.2 Política

- Repetições de polling e reinícios não criam novos trabalhos para uma identidade conhecida.
- Trabalhos transmitidos não são reenviados por falha de arquivamento.
- Trabalhos incertos não recebem retry automático.
- Exclusão de histórico detalhado preserva um registro mínimo da identidade consumida.
- Reimpressão cria novo trabalho ligado por `reprint_of`, com solicitante, justificativa e aviso de possível duplicidade.
- Reimpressão exige payload disponível e hash validado. Sem cópia local, recuperar o arquivo arquivado e validar; se indisponível, solicitar republicação com novo ID.

Não há garantia de exatamente uma impressão física com FTP, SQLite e TCP/RAW independentes. A política padrão prioriza evitar reenvio automático potencialmente duplicado; pode exigir intervenção para concluir uma solicitação.

## 11. Falhas, reinícios e intervenção

| Situação | Comportamento obrigatório |
|---|---|
| FTP indisponível | Backoff por conexão, preservar fila e continuar destinos independentes. |
| Credencial ou certificado inválido | Suspender conexão e alertar; não degradar para FTP. |
| Download interrompido | Descartar parcial e repetir download completo antes de qualquer envio. |
| Arquivo mudou durante leitura | Invalidar cópia, voltar à avaliação ou quarentena conforme contrato. |
| TCP recusado antes de escrita | Retry seguro dentro do limite. |
| Queda durante escrita | Resultado incerto; não reenviar automaticamente. |
| RAW aceito, impressora offline | Monitorar o job existente; não criar outro. |
| Arquivamento falhou | Repetir somente arquivamento. |
| Disco cheio ou banco sem escrita | Parar novos downloads/envios; manter evidências existentes. |
| Corrupção SQLite | Interromper processamento e exigir restauração/reconciliação. |
| Reinício em `ENVIANDO` | Reconciliar spooler quando aplicável; caso contrário, resultado incerto. |
| Payload de trabalho pendente ausente | Bloquear trabalho; recuperar com validação, nunca ignorar. |
| Limite de tentativas atingido | Manter pendente de intervenção, com erro e ação sugerida. |

Ao iniciar, obter exclusividade local, verificar migrações e integridade, reconciliar intenções de movimentação, arquivos locais e tentativas pendentes. Somente depois habilitar envio. Nunca “zerar” todos os estados para `PRONTO`.

No encerramento, parar descoberta, impedir novas tentativas e permitir drenagem limitada de operações em curso. Se interrompidas após possível efeito externo, registrar incerteza. Falha fatal deve terminar com código de erro para permitir recuperação pelo Windows; falhas operacionais de um trabalho não devem derrubar o serviço.

Resoluções manuais: confirmar impressão observada; autorizar nova tentativa com risco de duplicidade; descartar; ou manter bloqueado. Todas preservam histórico e justificativa. Pausar significa impedir novos envios, não desfazer os já iniciados.

## 12. Persistência e arquivos locais

### 12.1 Modelo lógico mínimo

| Tabela | Campos essenciais |
|---|---|
| `Connections` | ID, nome, host, porta, modo TLS, usuário, referência de segredo, timeouts, habilitada, revisão |
| `Printers` | ID, destino físico, tipo, host/porta ou fila, habilitada, revisão |
| `Mappings` | ID, conexão, impressora, caminhos, filtro, protocolo de publicação, polling, revisão |
| `Jobs` | UUID, identidade da origem, metadados, hash, tamanho, snapshot de destino, estado, etapa, caminho local/remoto, evidência, timestamps, `reprint_of` |
| `Attempts` | ID, job, etapa, início/fim, resultado, bytes conhecidos, erro, fila/job Windows, próxima tentativa |
| `RemoteOperations` | ID, job, tipo, origem, destino, estado da intenção, resultado e reconciliação |
| `ProcessedIdentities` | Chave única consumida, job, hash e decisão terminal mínima |
| `AuditEvents` | Data, identidade Windows, ação, entidade, motivo, correlação e mudanças sem segredos |
| `Settings` / `SchemaVersions` | Parâmetros globais, revisão e versão do esquema |

Usar chaves estrangeiras, índices por estado/próxima tentativa, mapeamento/identidade e horário. Datas persistidas em UTC; exibir no fuso do computador, com indicação no detalhe. Campos de duração devem usar relógio monotônico durante a execução.

### 12.2 SQLite

Banco em disco local, com WAL, `foreign_keys=ON`, `synchronous=FULL` e `busy_timeout` inicial de 5 segundos. Usar transações curtas e controle otimista por versão para alterações da interface. Em contenção persistente, pausar em vez de perder transições.

Não colocar o banco em compartilhamento de rede, OneDrive ou pasta sincronizada. O modo WAL exige coordenação local, conforme [SQLite — Write-Ahead Logging](https://www.sqlite.org/wal.html). Esses mecanismos melhoram durabilidade, mas não eliminam falhas de hardware ou a janela entre envio externo e gravação local.

### 12.3 Diretórios

```text
C:\Program Files\ZPL Print Manager\       executáveis e recursos
C:\ProgramData\ZPL Print Manager\
  data\manager.db                        banco local
  secrets\                              segredos protegidos
  spool\                                payloads por UUID
  quarantine\                           arquivos inválidos
  logs\                                 logs rotativos
  backups\                              backups controlados
```

Restringir ACLs ao serviço e administradores; acesso dos operadores deve ocorrer pela interface. Logs não devem conter senhas nem o conteúdo integral das etiquetas, que pode conter dados pessoais.

### 12.4 Backup e retenção

Criar backup consistente pelo mecanismo de backup do SQLite, com manifesto, versão de esquema e hashes dos payloads necessários. Para capturar banco e spool coerentes, pausar ingestão/envio, drenar operações e manter o bloqueio durante a captura. Não copiar somente o `.db` de um banco WAL ativo.

Não apagar payloads de trabalhos pendentes, incertos ou em quarentena por rotina de retenção. Retenção de logs: padrão de 30 dias e limite total de 500 MiB, ajustável. Alertar antes de atingir o limite de disco.

Restauração deve parar processamento, preservar o estado atual e executar reconciliação com FTP e spooler. Trabalhos que possam ter sido enviados depois da data do backup permanecem bloqueados até decisão. Credenciais protegidas para uma máquina não são automaticamente portáveis; migração para outro computador pode exigir redigitação.

## 13. Segurança e comunicação local

Executar o serviço com conta dedicada de menor privilégio, preferencialmente uma conta virtual de serviço quando compatível com a fila, evitando `LocalSystem` como padrão. Conceder somente acesso às pastas locais, destinos remotos e filas necessárias.

Proteger credenciais com Windows DPAPI no contexto da identidade do serviço, provisionadas por ele. O instalador deve validar que esse contexto funciona sem login interativo. Não armazenar senha em texto puro ou parâmetros de linha de comando. A proteção depende também de ACLs e não substitui segurança da máquina.

O Named Pipe deve aceitar somente clientes locais autorizados. Cada requisição deve conter versão, ID de correlação e tamanho limitado. Comandos mutáveis devem receber um ID de idempotência para impedir duplicação após perda de resposta. Validar identidade e papel do cliente no servidor, com impersonação restrita quando necessária.

Operações previstas: `GetStatus`, `ListJobs`, `GetJob`, `SaveConnection`, `TestConnection`, `SavePrinter`, `TestPrinterConnectivity`, `PrintTestLabel`, `SaveMapping`, `Pause`, `Resume`, `ResolveJob`, `Reprint`, `CreateBackup` e `ExportDiagnostics`. Testes de configuração devem usar o serviço e informar cada etapa validada.

ZPL pode conter comandos que alteram o equipamento, acessam memória ou controlam recursos. Aceitar arquivos somente de produtores autorizados e limitar permissões de escrita no FTP. Uma checagem de delimitadores não torna ZPL não confiável seguro. Perfis que restrinjam comandos precisam ser homologados por modelo e não devem modificar silenciosamente o payload.

## 14. Requisitos não funcionais e operação

- **Plataforma:** homologar Windows 11 x64 em versão suportada pelo .NET 10. Outras edições, inclusive Windows 10 LTSC e Windows Server, dependem da matriz oficial e de teste específico; não declarar compatibilidade genérica com todo Windows 10. Referência: [sistemas suportados pelo .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
- **Máquina de referência proposta:** 2 núcleos, 4 GB de RAM e 5 GB livres para aplicativo/dados, além do espaço exigido pelo Windows e pela retenção calculada. São critérios para teste, não consumo já medido.
- **Carga de homologação:** até 10 impressoras, 1.000 arquivos/dia e arquivos até 10 MiB. Revalidar em cargas superiores.
- **Meta de memória:** serviço abaixo de 200 MiB em operação nominal da carga de referência; medição obrigatória antes de afirmar atendimento.
- **Responsividade:** interface não bloqueia em consultas de rede ou impressão; mostrar progresso e cancelamento quando viável.
- **Latência:** em condições normais e sem fila, descobrir arquivo publicado até dois ciclos de polling; o tempo físico de impressão depende do equipamento.
- **Disponibilidade:** funcionamento contínuo com recuperação pelo Service Control Manager e nenhuma dependência de usuário conectado.
- **Observabilidade:** registrar job, etapa, destino, duração, código de erro e correlação; diferenciar erro de conexão, payload, dispositivo e arquivamento.
- **Backpressure:** limitar downloads e pausar ingestão quando faltar capacidade de disco ou a fila exceder limite configurado. Não descartar arquivos para manter desempenho.

Não incluir servidor web, broker externo ou servidor de banco na instalação básica.

## 15. Instalador, atualização e desinstalação

### 15.1 Instalação

1. Exigir elevação somente para instalar e configurar recursos administrativos.
2. Validar sistema operacional, arquitetura e espaço disponível.
3. Instalar binários autocontidos de .NET 10 em `Program Files`; o usuário não precisa instalar SDK.
4. Criar diretórios, grupos, ACLs e identidade do serviço.
5. Registrar `ZplPrintManager`, nome amigável “ZPL Print Manager”, com início automático atrasado.
6. Configurar recuperação após falhas: reinício em 1 e 5 minutos; falhas repetidas devem permanecer visíveis nos logs.
7. Instalar atalhos para o aplicativo visual e desinstalador.
8. Iniciar serviço sem mapeamentos ativos e abrir o assistente inicial.

Assinar instalador e binários distribuídos. Fixar versões de dependências, gerar inventário de componentes e revisar licenças. A publicação autocontida exige novas versões do produto para distribuir correções do runtime.

Não abrir porta TCP de entrada por padrão: o aplicativo usa pipe local e conexões de saída. Ambientes restritos precisam permitir FTP/FTPS e seu intervalo passivo, além dos destinos de impressão. Drivers de impressora são instalados separadamente, conforme fabricante.

### 15.2 Atualização

Pausar processamento, aguardar término limitado dos envios, registrar estados incertos, produzir backup consistente, parar serviço, substituir binários, migrar banco e verificar saúde antes de retomar. Migrações devem ser transacionais quando possível. Se falharem, manter serviço parado e preservar diagnóstico.

Rollback deve considerar binários e esquema compatível. Restaurar banco anterior depois de novos envios pode provocar duplicidade; exigir reconciliação nessa situação. Não atualizar automaticamente durante um envio.

### 15.3 Desinstalação

Parar/remover serviço e binários. Preservar configuração, histórico e payloads por padrão. Oferecer exclusão explícita dos dados, com aviso e opção de backup. Nunca apagar arquivos remotos de entrada ou `/impressos` como parte da desinstalação.

## 16. Plano de testes e aceitação

### 16.1 Testes automatizados

| ID | Cenário | Resultado esperado |
|---|---|---|
| T-01 | Mesmo arquivo em múltiplos polls | Um único trabalho. |
| T-02 | Dois IDs diferentes com conteúdo igual | Dois trabalhos legítimos. |
| T-03 | Mesmo ID com conteúdo divergente | Conflito, sem impressão automática. |
| T-04 | Arquivo temporário/upload lento | Nenhum envio antes de elegibilidade. |
| T-05 | Queda no download ou tamanho excedido | Parcial descartado; nenhum envio. |
| T-06 | Traversal no nome remoto | Nenhuma escrita fora do spool. |
| T-07 | Escritas TCP/RAW parciais | Contabilização correta e incerteza quando aplicável. |
| T-08 | Falha antes da primeira escrita | Retry limitado sem duplicar trabalho. |
| T-09 | Falha depois de possível envio | Bloqueio; nenhum retry automático. |
| T-10 | Envio concluído e rename falha | Somente arquivamento é repetido. |
| T-11 | Rename realizado e resposta perdida | Reconciliação sem sobrescrita/reenvio. |
| T-12 | Reinício em cada fronteira persistida | Recuperação coerente com a etapa. |
| T-13 | Disco cheio/banco indisponível | Suspensão segura e alerta. |
| T-14 | Mapeamentos para mesma impressora | Nenhuma intercalação de payloads. |
| T-15 | Alteração de destino com fila existente | Snapshot preservado. |
| T-16 | Comando IPC repetido ou não autorizado | Idempotência ou rejeição, com auditoria. |
| T-17 | Retenção e reimpressão | Preservação de pendências e vínculo ao original. |

Usar testes de unidade para transições e política de retry; integração com SQLite real, servidor FTP/FTPS controlado e receptor TCP que captura bytes e injeta quedas. Comparar o SHA-256 do payload transmitido com o original. Um receptor TCP simulado prova fidelidade de bytes, não impressão física.

### 16.2 Testes em Windows e hardware

- FTP e FTPS explícito/implícito conforme opções suportadas; certificado válido, expirado, hostname incorreto e CA interna.
- Permissões remotas insuficientes, colisão de destino, arquivos grandes e nomes Unicode.
- Impressora ZPL em rede e impressora ZPL USB com fila RAW, sob a conta real do serviço e sem usuário conectado.
- Falta de papel, tampa aberta, equipamento desligado, cabo desconectado e reinício do spooler.
- Etiquetas com acentos, imagens e múltiplas etiquetas/quantidades; fidelidade depende da codificação e recursos do equipamento.
- Interrupção forçada entre envio e commit, entre rename e commit e durante atualização.
- Suspensão/retomada do Windows, reinício do computador e indisponibilidade prolongada de FTP.
- Restauração de backup antigo, migração para outra máquina e reconfiguração de credenciais.
- Instalação limpa, upgrade, falha de migração, reparo e desinstalação com preservação de dados.
- Teste contínuo de 72 horas na carga de referência, verificando memória, handles, disco, logs e ausência de trabalhos perdidos.

### 16.3 Critérios de aceite do produto

1. Um operador configura uma conexão, uma pasta e uma impressora somente pela interface.
2. Fechar a interface ou encerrar a sessão não interrompe o serviço.
3. Cada arquivo elegível chega ao destino configurado com bytes preservados.
4. Arquivos incompletos ou inválidos não são enviados.
5. Reinícios e polling repetido não causam reenvio de trabalhos já transmitidos conhecidos.
6. Falha de arquivamento não provoca nova impressão.
7. Falhas com possível efeito externo ficam explicitamente incertas e exigem decisão auditada.
8. O painel nunca apresenta transmissão TCP ou aceitação RAW como prova física automática.
9. Credenciais não aparecem em banco em texto puro, logs ou exportações.
10. Instalação, atualização e recuperação passam pelos testes em Windows e hardware homologados.

## 17. Entregáveis de implementação e decisões de homologação

**Entregáveis:** solução C# organizada em camadas; interface WinForms; serviço; adaptadores; migrações SQLite; instalador assinado; testes; manual do operador; guia de implantação; procedimento de backup/recuperação; inventário de dependências e relatório de homologação.

**Sequência sugerida:** núcleo persistente e simulação de falhas; FTP/download; TCP e RAW; interface e autorização; instalador; testes físicos e recuperação. Concluir a política de estados e incerteza antes de habilitar processamento automático em produção.

Antes da liberação, registrar: modelos e firmwares das impressoras, drivers/filas RAW, servidor FTP e suporte a rename, volume real, tamanhos máximos, política de retenção, protocolo do produtor e eventual mecanismo de confirmação física. A falta de confirmação física não impede a versão básica, desde que a interface preserve a semântica de transmissão.

## 18. Glossário

- **ZPL:** linguagem de comandos de impressão de etiquetas.
- **FTPS:** FTP protegido por TLS.
- **Polling:** consulta periódica por novos arquivos.
- **RAW:** envio de dados na linguagem compreendida pela impressora, sem renderização gráfica pelo aplicativo.
- **Spooler:** componente Windows que recebe e gerencia trabalhos de impressão.
- **Payload:** bytes originais do arquivo a transmitir.
- **Idempotência:** repetição de uma operação sem multiplicar seu efeito lógico.
- **Resultado incerto:** situação em que o sistema não consegue provar se o trabalho foi executado total ou parcialmente.
- **Reconciliação:** comparação das evidências locais e externas para resolver uma operação interrompida.
- **Quarentena:** retenção de um arquivo impedido de seguir automaticamente.

---

Este documento especifica o comportamento a implementar. Não representa um aplicativo já desenvolvido nem resultados de testes já executados. Os padrões numéricos são propostas de configuração e os critérios de desempenho precisam ser medidos na homologação.

## 19. Desenvolvimento no Ubuntu e publicação para Windows

**Adendo de 20 de outubro de 2026.** Esta seção complementa a especificação, cujo conteúdo anterior foi preservado integralmente. Para a distribuição descrita aqui, **Inno Setup com instalador `.exe` substitui a proposta WiX/MSI da seção 3**. Os requisitos de instalação, atualização e desinstalação da seção 15 continuam obrigatórios. “Manager” nesta seção corresponde ao aplicativo visual chamado “Desktop” na seção 3.1.

### 19.1 Estratégia Linux-first

O ambiente principal de desenvolvimento será Ubuntu com **.NET 10 SDK**. A lógica independente do sistema operacional deve ser escrita, compilada e testada localmente. O produto final continua sendo Windows: a interface WinForms, a integração com Windows Service e o spooler exigem validação nesse sistema.

O ciclo recomendado é: desenvolver e testar o núcleo no Ubuntu; verificar a compilação cruzada quando possível; enviar as alterações ao GitHub; compilar, testar e empacotar em `windows-latest`; homologar o artefato resultante em VM Windows e impressoras reais antes da liberação.

Não é necessário usar uma máquina Windows como estação principal. É necessário ter acesso a Windows para validar os componentes específicos e a instalação. Um runner hospedado compila o produto, mas não substitui uma sessão interativa e os dispositivos do ambiente de operação.

### 19.2 Separação Core / Service / Manager WinForms

Estrutura de referência para os comandos desta seção; ajustar nomes e caminhos à solução implementada:

```text
ZplPrintManager.slnx
 global.json
 src/
   ZplPrintManager.Core/                net10.0
   ZplPrintManager.Contracts/           net10.0
   ZplPrintManager.Infrastructure/      net10.0
   ZplPrintManager.Infrastructure.Windows/ net10.0-windows
   ZplPrintManager.Service/             net10.0-windows
   ZplPrintManager.Manager/             net10.0-windows + WinForms
 tests/
   ZplPrintManager.Core.Tests/          net10.0
   ZplPrintManager.Infrastructure.Tests/ net10.0
   ZplPrintManager.Windows.Tests/       net10.0-windows
 installer/
   ZplPrintManager.iss
 .github/workflows/build.yml
```

| Componente | Responsabilidade e limite |
|---|---|
| Core | Estados, políticas de retry, deduplicação, coordenação e interfaces de acesso externo. Sem WinForms, spooler, DPAPI, Event Log ou Service Control Manager. |
| Service | Host do processamento em segundo plano, ciclo de vida do serviço, composição dos adaptadores e comunicação autorizada com a interface. Referencia Core; não referencia Manager. |
| Manager WinForms | Configuração visual, consulta de status e comandos via IPC. Referencia contratos; não assume a propriedade do banco nem do processamento. |
| Infrastructure | Implementações portáveis de FTP/FTPS, SQLite, arquivos e TCP, desde que suas dependências suportem Linux e Windows. |
| Infrastructure.Windows | RAW/Win32, proteção de credenciais, ACLs, identidade Windows e demais integrações específicas. |
| Contracts | Mensagens de configuração/status e versões do protocolo, sem tipos de controles visuais. |

Core não deve referenciar Infrastructure: as interfaces pertencem ao núcleo e os adaptadores as implementam. Service e Manager são executáveis separados; fechar a interface não encerra o serviço. Para simular o processamento no Ubuntu, usar testes ou um host de desenvolvimento portável com adaptadores falsos, sem executar o host Windows de produção.

A integração do serviço utiliza `Microsoft.Extensions.Hosting.WindowsServices`, com versão compatível fixada, e o modelo `BackgroundService`/Generic Host. A configuração do host não registra automaticamente o serviço no Windows; esse registro pertence ao instalador. Referência: [serviços Windows em .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service).

### 19.3 Preparação do Ubuntu com .NET 10 SDK

Usar uma versão do Ubuntu suportada pelo .NET 10 e instalar suas bibliotecas nativas requeridas. Uma opção para desenvolvimento é o script oficial, instalado no diretório do usuário:

```bash
mkdir -p work
curl --fail --show-error --location https://dot.net/v1/dotnet-install.sh \
  --output work/dotnet-install.sh
bash work/dotnet-install.sh --channel 10.0 --quality GA \
  --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --info
dotnet --list-sdks
```

Persistir `DOTNET_ROOT` e a inclusão no `PATH` no perfil do shell. O script não instala dependências nativas do sistema; seguir a documentação para a versão exata do Ubuntu. Se o SDK já for instalado pelo gerenciador de pacotes, evitar misturar instalações e verificar qual `dotnet` está sendo usado. Referência: [instalação oficial no Linux](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual).

Após selecionar e validar um SDK 10.0, criar `global.json` na raiz, se ainda não existir:

```bash
dotnet new globaljson --sdk-version "$(dotnet --version)" --roll-forward disable
```

Executar esse comando somente após confirmar que a versão selecionada é 10.0.x. Versionar o arquivo e atualizar o SDK deliberadamente, mantendo Ubuntu e CI alinhados. Não sobrescrever um `global.json` existente sem revisar sua política. O SDK é uma ferramenta de desenvolvimento; a instalação final autocontida não exige SDK no computador do operador.

### 19.4 Testes locais no Ubuntu

Executar os projetos portáveis explicitamente, evitando uma execução indiscriminada de toda a solução com testes Windows:

```bash
dotnet restore tests/ZplPrintManager.Core.Tests/ZplPrintManager.Core.Tests.csproj
dotnet test tests/ZplPrintManager.Core.Tests/ZplPrintManager.Core.Tests.csproj \
  -c Release --no-restore

dotnet restore tests/ZplPrintManager.Infrastructure.Tests/ZplPrintManager.Infrastructure.Tests.csproj
dotnet test tests/ZplPrintManager.Infrastructure.Tests/ZplPrintManager.Infrastructure.Tests.csproj \
  -c Release --no-restore
```

Validar estados, deduplicação, retries, aquisição, downloads parciais, reconciliação e arquivamento sem reenvio. Usar SQLite temporário real, um servidor FTP/FTPS de teste e um receptor TCP controlado para conferir os bytes ZPL. As fixtures devem provisionar esses recursos ou informar sua configuração; nunca apontar testes automatizados para filas ou FTP de produção.

Separar testes de integrações nativas Windows em outro projeto. Simulações de impressão no Ubuntu verificam lógica e transmissão, mas não comprovam funcionamento do spooler nem saída física da etiqueta. Não substituir a impressão Windows por CUPS e considerar isso homologação do produto.

### 19.5 Cross-build WinForms com EnableWindowsTargeting

O projeto Manager deve declarar WinForms e um framework Windows. Exemplo mínimo de propriedades, a incorporar ao projeto existente:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Como alternativa à propriedade no projeto, passar `-p:EnableWindowsTargeting=true` em restore, build e publish no Ubuntu. Ela permite solicitar os pacotes de referência Windows ao compilar fora de Windows e trata o erro `NETSDK1100`; **não permite executar WinForms no Linux**. Referência: [EnableWindowsTargeting e NETSDK1100](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100).

O cross-build depende da disponibilidade dos targeting packs, runtime packs, fontes NuGet e dependências compatíveis. Bibliotecas nativas, controles de terceiros, geradores ou tarefas MSBuild que executem ferramentas Windows podem impedir a compilação no Ubuntu. Nesse caso, compilar esses projetos no runner Windows, mantendo os testes portáveis locais. O designer visual WinForms deve ser usado em um ambiente Windows compatível; editar o código no Ubuntu não fornece o designer nem valida a UI.

Na raiz da solução, compilar os projetos Windows para x64:

```bash
dotnet restore ZplPrintManager.slnx -r win-x64 -p:EnableWindowsTargeting=true
dotnet build ZplPrintManager.slnx -c Release -r win-x64 \
  -p:EnableWindowsTargeting=true --no-restore
```

Esse restore com RID Windows é distinto do restore dos testes executados no Linux. Reexecutar o restore apropriado ao alternar entre os dois fluxos. O build bem-sucedido apenas verifica a compilação; a validação Windows continua obrigatória.

### 19.6 Publish win-x64

Publicar cada executável em diretório separado para evitar colisões de arquivos:

```bash
dotnet publish src/ZplPrintManager.Service/ZplPrintManager.Service.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false -o artifacts/publish/service

dotnet publish src/ZplPrintManager.Manager/ZplPrintManager.Manager.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false -o artifacts/publish/manager
```

Os comandos fazem o restore necessário para a publicação. `win-x64` define o destino Windows x64; `--self-contained true` inclui o runtime. Distribuir todo o conteúdo das pastas, incluindo DLLs nativas, recursos e arquivos de configuração necessários. O exemplo desativa trimming e ReadyToRun e não solicita Native AOT nem arquivo único, reduzindo variáveis de compatibilidade na primeira versão.

Uma publicação dependente de framework usa `--self-contained false` e exige os runtimes apropriados no destino, incluindo .NET Desktop Runtime 10 para WinForms. A estratégia adotada aqui é autocontida e exige republicação do produto para atualizar o runtime incluído. Referência: [dotnet publish](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish).

### 19.7 GitHub Actions: compilar, testar e empacotar em Windows

Exemplo de `.github/workflows/build.yml`. Pressupõe os projetos acima, testes baseados em VSTest com `Microsoft.NET.Test.Sdk`, `global.json` versionado e script Inno Setup implementado. O projeto Windows.Tests deve usar a categoria `WindowsVM` para testes que dependem de sessão interativa, instalação de serviço ou hardware; os demais devem rodar sem esses recursos.

```yaml
name: ZPL Print Manager CI

on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  portable-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json
      - name: Testar Core
        run: dotnet test tests/ZplPrintManager.Core.Tests/ZplPrintManager.Core.Tests.csproj -c Release
      - name: Testar infraestrutura portável
        run: dotnet test tests/ZplPrintManager.Infrastructure.Tests/ZplPrintManager.Infrastructure.Tests.csproj -c Release

  windows-build:
    needs: portable-tests
    runs-on: windows-latest
    defaults:
      run:
        shell: pwsh
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json
      - name: Restaurar
        run: dotnet restore ZplPrintManager.slnx
      - name: Compilar
        run: dotnet build ZplPrintManager.slnx -c Release --no-restore
      - name: Testar componentes em Windows
        run: dotnet test ZplPrintManager.slnx -c Release --no-build --filter "Category!=WindowsVM" --logger trx --results-directory artifacts/test-results
      - name: Publicar Service
        run: dotnet publish src/ZplPrintManager.Service/ZplPrintManager.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o artifacts/publish/service
      - name: Publicar Manager
        run: dotnet publish src/ZplPrintManager.Manager/ZplPrintManager.Manager.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o artifacts/publish/manager
      - name: Localizar compilador Inno Setup
        run: |
          $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
          if (!(Test-Path $iscc)) {
            throw 'Inno Setup 6 ausente. Provisionar a versão homologada no runner.'
          }
          "ISCC_PATH=$iscc" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
      - name: Compilar instalador de teste
        run: |
          New-Item -ItemType Directory -Force artifacts/installer | Out-Null
          & $env:ISCC_PATH "/DAppVersion=0.0.${{ github.run_number }}" "/Oartifacts/installer" installer/ZplPrintManager.iss
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
      - name: Guardar resultados dos testes
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-windows
          path: artifacts/test-results
          if-no-files-found: warn
      - name: Guardar pacote Windows
        uses: actions/upload-artifact@v4
        with:
          name: zpl-print-manager-win-x64
          path: |
            artifacts/publish/
            artifacts/installer/
          if-no-files-found: error
```

O exemplo gera um artefato de CI, **não uma release automaticamente aprovada**. A versão `0.0.<run_number>` é exclusiva para builds de teste; releases devem usar a versão validada da tag e aplicá-la também aos executáveis. Os testes excluídos pela categoria `WindowsVM` precisam ser executados em outra etapa de homologação, não descartados.

As versões principais das actions tornam o exemplo legível; no pipeline de produção, fixar commits revisados e atualizar de forma controlada. O alias `windows-latest` e os programas pré-instalados podem mudar: verificar o inventário da imagem e provisionar uma versão aprovada do Inno Setup quando necessário, com integridade verificada. O workflow falha explicitamente se não encontrar o compilador esperado. Referências: [setup-dotnet](https://github.com/actions/setup-dotnet), [imagens dos runners](https://github.com/actions/runner-images), [checkout](https://github.com/actions/checkout) e [upload-artifact](https://github.com/actions/upload-artifact).

### 19.8 Instalador com Inno Setup

Manter `installer/ZplPrintManager.iss` no repositório. O script deve aceitar `AppVersion` via preprocessor e incluir as duas pastas publicadas, preservando subdiretórios. Caminhos `Source` são relativos ao script: por exemplo, `..\artifacts\publish\service\*` para `{app}\service` e `..\artifacts\publish\manager\*` para `{app}\manager`, com `recursesubdirs createallsubdirs`.

Compilação manual em PowerShell no Windows, a partir da raiz do repositório:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" `
  /DAppVersion=1.0.0 /Oartifacts/installer installer/ZplPrintManager.iss
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação do instalador' }
```

O Inno Setup gera um instalador `.exe`, não MSI. O script deve implementar efetivamente registro e remoção do serviço, início automático atrasado, recuperação, identidade, diretórios e ACLs, atalhos, atualização e preservação de dados. Copiar executáveis não instala um Windows Service. Falhas de comandos administrativos devem interromper a instalação com diagnóstico, sem declarar sucesso parcial. Referência: [compilador de linha de comando do Inno Setup](https://jrsoftware.org/ishelp/topic_compilercmdline.htm).

O instalador exige elevação, mas o Manager deve funcionar sem elevação permanente. Ao abrir a interface ao final, usar o contexto do usuário original. Nas atualizações, parar o serviço antes de substituir arquivos, seguir o backup e a reconciliação da seção 15 e preservar ProgramData. Assinar os binários antes do empacotamento e o instalador final depois; a assinatura e seus segredos pertencem à etapa protegida de release.

### 19.9 Homologação em VM Windows e impressoras

Manter VM Windows x64 compatível com .NET 10, com snapshots e uma sessão interativa para a UI. A máquina deve alcançar o FTP de teste e as impressoras; para USB, configurar passthrough e o driver correto. Quando a virtualização não representar o dispositivo adequadamente, usar uma máquina Windows física de homologação.

| Ambiente | Verificações obrigatórias |
|---|---|
| Ubuntu local/CI | Regras Core, contratos, SQLite portável, FTP/FTPS e receptor TCP simulado, incluindo falhas e recuperação. |
| Runner Windows | Build completo, testes automatizados sem hardware, publicação x64 e compilação do instalador. |
| VM Windows | Instalação limpa sem SDK, UI WinForms, DPI e validações, IPC/autorização, serviço sem login, reinícios, Event Log, DPAPI/ACLs, upgrade e desinstalação. |
| Windows com dispositivos | Fila RAW/spooler, identidade real do serviço, impressora ZPL USB e TCP, bytes e saída física, falta de papel, desconexões e resultados incertos. |

Instalar exatamente o artefato produzido pela CI. Validar o serviço iniciado pelo Service Control Manager, não apenas executar o `.exe` manualmente. Confirmar acesso às filas sob a identidade do serviço e operação após logout. Testar interrupções durante envio e arquivamento, reinício do spooler e ausência de reenvio automático em resultado incerto, conforme seções 8 a 11 e 16.

A aceitação no spooler ou no socket continua sendo evidência de transmissão, não de etiqueta física. Guardar modelo, firmware, driver, versão Windows e resultados de homologação junto à versão testada.

### 19.10 Requisitos de CI/CD e liberação

1. **Reprodutibilidade:** versionar solução, projetos, `global.json`, workflow, script `.iss` e dependências. Adotar arquivos de lock NuGet; após gerá-los e versioná-los, usar restore em modo bloqueado e manter os restores implícitos de publish sob a mesma política. Fixar e registrar a versão do compilador Inno Setup.
2. **Verificações obrigatórias:** exigir sucesso dos jobs Linux e Windows para integração; impedir release se houver falhas, testes obrigatórios ausentes ou homologação Windows pendente. Publicar relatórios mesmo quando testes falharem.
3. **Infraestrutura:** acesso aos feeds NuGet e downloads aprovados, espaço de artefatos e minutos de CI; fixtures FTP/TCP isoladas; VM e impressoras de homologação disponíveis. Não presumir que runners hospedados tenham drivers, impressoras ou acesso à rede interna.
4. **Separação de privilégios:** PRs compilam e testam sem segredos de produção. Assinatura e publicação usam ambiente protegido, credenciais restritas e somente código aprovado. Runner interno com hardware não deve executar código de PR não confiável.
5. **Rastreabilidade:** associar versão, commit, SDK, dependências, imagem de runner, logs de testes e hashes SHA-256 ao pacote. Gerar inventário de componentes e conferir licenças e vulnerabilidades antes da entrega.
6. **Distribuição:** disponibilizar instalador assinado, checksums, notas da versão e requisitos do Windows apenas após homologação. A etapa de CD deve promover o mesmo pacote validado, sem recompilação silenciosa. O YAML acima cobre CI e armazenamento de artefatos; publicação de release e assinatura precisam ser implementadas separadamente.
7. **Manutenção:** republicar os executáveis autocontidos para incorporar correções do .NET e bibliotecas. Testar migração, backup, recuperação e rollback compatível antes de cada atualização distribuída.

Esta seção é documentação e exemplo de implementação. Seus comandos, workflow e instalador devem ser adaptados ao repositório real; não representam compilação, execução de testes ou homologação já realizadas.
