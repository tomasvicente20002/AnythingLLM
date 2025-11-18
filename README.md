# AnythingLLM sync helper

Aplicação de consola em .NET 8 que autentica no AnythingLLM e faz upload apenas dos ficheiros alterados. Os ficheiros são monitorizados através de *fingerprints* (SHA-256) guardados em disco para evitar reenvios desnecessários.

## Requisitos

- .NET 8 SDK
- Instância AnythingLLM acessível por HTTP
- Uma chave API com permissões para gerir documentos e workspaces

## Configuração

A forma mais simples é editar o ficheiro [`appsettings.json`](./appsettings.json). Exemplo:

```json
{
  "baseUrl": "http://localhost:3001",
  "apiKey": "YOUR-ANYTHINGLLM-API-KEY",
  "directory": "K:/n8n/RagFiles",
  "workspace": "HTS",
  "fingerprintPath": "fingerprints.json",
  "maxConcurrency": 4,
  "additionalWorkspaces": [],
  "metadata": {
    "source": "sync-job"
  },
  "dryRun": false
}
```

Também é possível configurar tudo através de argumentos de linha de comandos ou variáveis de ambiente. A ordem de precedência é: argumentos → variáveis de ambiente → `appsettings.json`.

| Chave | Argumento | Variável de ambiente | Descrição |
|-------|-----------|----------------------|-----------|
| `baseUrl` | `--baseUrl` | `ANYTHINGLLM_BASE_URL` | URL base da API |
| `apiKey` | `--apiKey` | `ANYTHINGLLM_API_KEY` | Chave de autenticação |
| `directory` | `--directory` | `ANYTHINGLLM_DIRECTORY` | Pasta raiz a sincronizar |
| `workspace` | `--workspace` | `ANYTHINGLLM_WORKSPACE` | Workspace principal |
| `additionalWorkspaces` | `--additionalWorkspaces="ws1,ws2"` | `ANYTHINGLLM_ADDITIONAL_WORKSPACES` | Workspaces extra para onde o documento será associado |
| `fingerprintPath` | `--fingerprintPath` | `ANYTHINGLLM_FINGERPRINTS` | Caminho do ficheiro JSON com os hashes |
| `maxConcurrency` | `--maxConcurrency` | `ANYTHINGLLM_MAX_CONCURRENCY` | Limite de uploads concorrentes |
| `metadata` | `--metadata '{"key":"value"}'` | — | JSON adicional enviado com o documento |
| `dryRun` | `--dryRun true` | `ANYTHINGLLM_DRY_RUN` | Se verdadeiro, não faz upload nem grava fingerprints |

Use `--config outro.json` para carregar um ficheiro de configuração alternativo.

## Execução

```bash
cd AnythingLLM
 dotnet run --project AnythingLLM -- --directory "/caminho/para/files" --workspace "HTS"
```

Os argumentos após `--` são encaminhados para a aplicação. Caso tenha tudo definido em `appsettings.json`, basta executar `dotnet run --project AnythingLLM`.

### Dry-run

Execute com `--dryRun true` para validar a configuração e ver os ficheiros que seriam enviados, sem mexer nos dados existentes.

## Funcionamento

1. Autentica no AnythingLLM usando a API key.
2. Procura todos os ficheiros dentro da pasta configurada.
3. Calcula o hash de cada ficheiro e compara com o histórico guardado.
4. Só agenda uploads para ficheiros novos ou alterados.
5. Os uploads são feitos de forma concorrente respeitando `maxConcurrency`.
6. No fim, o ficheiro de fingerprints é actualizado (excepto em `dryRun`).

## Desenvolvimento

```bash
# Restaurar dependências e verificar se o código compila
 dotnet build AnythingLLM/AnythingLLM.csproj
```

Sinta-se à vontade para abrir *issues* ou *merge requests* com melhorias.
