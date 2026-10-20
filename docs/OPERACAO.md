# Operação do ZPL Print Manager

## Instalação e acesso

1. Use o instalador x64 produzido pela CI, depois da homologação do pacote. O computador de destino não precisa do SDK.
2. Instale os drivers e filas RAW para uso da máquina. Conceda à conta `NT SERVICE\ZplPrintManager` acesso às filas e aos recursos necessários. Não use uma fila disponível somente no perfil interativo.
3. Um administrador concede acesso usando os grupos locais abaixo. Feche a sessão e entre novamente após alterar grupos para renovar o token Windows.

```powershell
Add-LocalGroupMember -Group 'ZplPrintManager Administrators' -Member 'DOMINIO\responsavel'
Add-LocalGroupMember -Group 'ZplPrintManager Operators' -Member 'DOMINIO\operador'
Add-LocalGroupMember -Group 'ZplPrintManager Readers' -Member 'DOMINIO\consulta'
```

Administradores configuram conexões, impressoras, mapeamentos, parâmetros, backups e diagnóstico. Operadores pausam, retomam, resolvem trabalhos e solicitam reimpressões/etiquetas de teste. Leitores consultam painel e histórico. O serviço valida o token em cada requisição. O Manager não precisa permanecer elevado; o botão de início do serviço solicita elevação apenas para essa ação.

## Primeira configuração

1. Abra **Painel → Assistente inicial**. Informe host, usuário e senha FTP/FTPS. `Explicit` significa FTPS explícito; `Implicit`, FTPS implícito; `None`, FTP sem criptografia. O padrão é explícito, porta 21. Não há SFTP nem aceitação de certificado inválido.
2. Cadastre uma impressora: `Tcp` usa host/porta; `WindowsRaw` usa o nome exato da fila. Use o mesmo **identificador do equipamento físico** para entradas TCP/USB do mesmo equipamento.
3. Use **Testar conexão**. O teste de impressora não imprime. Antes de configurar pastas, o teste FTP valida apenas conexão/autenticação.
4. Cadastre o mapeamento, selecione conexão e impressora, confira os caminhos e ative-o. As quatro pastas precisam existir e permitir criar/ler/renomear/remover um arquivo temporário exclusivo de teste. A ativação falha se esses testes falharem.
5. O produtor deve enviar `ID.uploading`, fechar a transferência e renomear para `ID.zpl`. Não reutilize IDs. O modo `StableMetadata` exige três observações por pelo menos dez segundos e tem garantia reduzida.
6. Selecione a impressora na aba **Impressoras** e solicite **Administração → Etiqueta de teste**, informando o motivo. A ação consome mídia e entra na fila auditada.
7. Retome o processamento no painel. O serviço funciona sem usuário conectado; fechar o Manager não o interrompe.

Senha vazia em edição preserva o segredo existente. Desative itens pela opção **Ativo**; não há exclusão de configurações referenciadas pelo histórico. Alterações usam revisão otimista. Trabalhos descobertos preservam snapshots de conexão, mapeamento e destino; mudanças posteriores não redirecionam a fila.

## Falhas e intervenções

- **RESULTADO_INCERTO:** algum conteúdo pode ter sido impresso. Verifique etiquetas e fila do Windows antes de confirmar impressão, descartar ou autorizar reimpressão.
- **ARQUIVAMENTO_PENDENTE:** o transporte aceitou o trabalho. A repetição automática movimenta somente o arquivo remoto. Após esgotar tentativas, use **Repetir arquivamento**.
- **QUARENTENA:** confira tamanho, conteúdo, hash, origem/destino e tentativas. Arquivos inválidos nunca entram automaticamente em envio. A movimentação para a pasta de erros não autoriza impressão.
- **Reimprimir:** gera outro UUID vinculado ao original, com justificativa e identidade Windows. A cópia local ou arquivada precisa corresponder ao hash. Um original incerto é encerrado como substituído para liberar a fila. Reimpressão pode duplicar etiquetas.
- **Pausar:** impede novos envios; não desfaz uma transmissão iniciada. Outros destinos físicos podem continuar quando uma impressora está bloqueada.

O histórico exibe os mil trabalhos mais recentes; filtros usam horário local. Detalhes e CSV mantêm timestamps UTC. A evidência de transporte é separada de confirmação pelo operador.

## Dados, backups e restauração

Dados ficam em `%ProgramData%\ZPL Print Manager`: `data`, `secrets`, `spool`, `quarantine`, `logs` e `backups`. ACLs permitem acesso direto apenas ao serviço, SYSTEM e administradores. O banco usa WAL e não deve ser colocado em compartilhamento ou pasta sincronizada.

**Criar backup** suspende a admissão de operações, aguarda drenagem e usa a API de backup SQLite, copiando payloads, quarentena e segredos protegidos, com manifesto SHA-256. O backup não inclui arquivos remotos nem representa prova de impressão. Copie a pasta completa para armazenamento protegido; não copie somente `manager.db` de um banco em uso.

**Restaurar backup** recebe o nome UUID da pasta de backup e um motivo. Valida hashes, preserva um backup do estado anterior, prepara um banco temporário, mantém identidades e comandos posteriores conhecidos e restaura com processamento pausado. Trabalhos não terminais ficam incertos e exigem reconciliação. Arquivos locais existentes não são sobrescritos. Credenciais existentes são preservadas; segredos ausentes podem ser recuperados do backup. DPAPI depende da máquina e da identidade; confira ou redigite as credenciais, especialmente após migração ou mudanças posteriores ao backup.

**Exportar diagnóstico** gera um JSON com versão, capacidade, contagens e IDs/estados pendentes, sem credenciais, endereços ou conteúdo ZPL. O CSV neutraliza fórmulas de planilha.

## Atualização e desinstalação

O instalador solicita pausa e backup ao serviço anterior, espera sua parada, substitui binários e verifica saúde. Se o serviço anterior não consegue iniciar ou produzir backup, a atualização aborta; preserve dados e investigue antes de tentar novamente. O serviço permanece pausado para conferência após atualização. Downgrade de banco com versão superior é recusado.

A desinstalação para e remove o serviço e os binários, preservando dados, grupos e arquivos remotos. Para apagar dados deliberadamente, faça e verifique um backup, desinstale e remova a pasta ProgramData manualmente como administrador. Nenhuma rotina apaga arquivos FTP de produção.
