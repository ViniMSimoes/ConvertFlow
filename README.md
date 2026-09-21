# ConvertFlow 🚀

> **Conversor Nativo de Arquivos Bancários e Planilhas para Windows**  
> 100% autônomo, offline, sem navegador, sem servidor web e compatível com o padrão **TOTVS Gestão Financeira (Linha RM / Protheus)**.

---

## 📌 Visão Geral

O **ConvertFlow** é um aplicativo desktop para Windows desenvolvido em C# e WPF. Ele foi projetado para ser **extremamente leve (~45 KB)**, rápido e direto ao ponto: basta arrastar o arquivo, escolher o formato desejado e clicar em converter.

Todos os arquivos convertidos são salvos automaticamente na pasta:
```
Documentos\Arquivos_Convertidos\
```

---

## ✨ Principais Funcionalidades

- **🏦 Conciliação Bancária TOTVS (CSV / XLSX ➔ OFX)**:
  - Gera arquivos OFX com tags estritamente fechadas (`<BANKID>`, `<BRANCHID>`, `<ACCTID>`, `<DTPOSTED>`, `<TRNAMT>`, `<FITID>`, `<CHECKNUM>`, `<MEMO>`, etc.).
  - Mapeamento direto com as tabelas do TOTVS (`FCXA` - Conta/Caixa e `FXCX` - Extrato Bancário).
  - Campos de Banco, Agência e Conta configuráveis na interface com memorização automática.
- **📊 Planilhas Excel Nativas (CSV / OFX ➔ XLSX)**:
  - Geração nativa OpenXML SpreadsheetML sem necessidade de ter o Microsoft Office instalado.
  - **Auto-Ajuste de Colunas**: Cada coluna calcula dinamicamente a sua largura ideal com base no maior texto contido, evitando células cortadas.
  - Cabeçalhos formatados em negrito com fundo suave e borda delimitadora.
  - Valores monetários formatados como números reais (permitindo uso imediato de fórmulas e somas no Excel).
- **🔤 Correção Inteligente de Acentuação (Encoding)**:
  - Detecção automática de codificação de entrada: reconhece **UTF-8 (com ou sem BOM)** e **Windows-1252 (ANSI)**.
  - Gravação de arquivos OFX em **Windows-1252 (`CHARSET:1252`)**, garantindo que acentos como `é`, `ã`, `ç`, `á` apareçam corretamente no TOTVS (evitando o erro clássico de interrogações como `Cr?dito`).
- **📄 Relatórios em PDF e TXT**:
  - Geração de PDF vetorial puro (padrão PDF 1.4) formatado em tabela com paginação.
  - Geração de TXT tabulado e perfeitamente alinhado.
- **🔄 Conversão Bidirecional Completa**:
  - `CSV` ➔ `OFX`, `XLSX`, `PDF`, `TXT`
  - `OFX` ➔ `CSV`, `XLSX`, `PDF`, `TXT`
  - `XLSX` ➔ `OFX`, `CSV`, `PDF`, `TXT`

---

## 🏢 Mapeamento TOTVS Gestão Financeira

| Tag OFX | Descrição | Campo TOTVS RM / Protheus |
| :--- | :--- | :--- |
| `<BANKID>` | Número do Banco | `FCXA.NUMBANCO` |
| `<BRANCHID>` | Código da Agência | `FCXA.CODAGENCIA` |
| `<ACCTID>` | Conta Corrente | `FCXA.CODCONTA` |
| `<DTPOSTED>` | Data da Movimentação | `FXCX.DATA` |
| `<TRNAMT>` | Valor da Transação | `FXCX.VALOR` |
| `<FITID>` | Identificador / Doc | `FXCX.NUMEROODOCUMENTO` |
| `<MEMO>` | Histórico da Transação | `FXCX.HISTORICO` |
| `<BALAMT>` | Saldo Bancário | `FCXA.SALDOBANCARIO` |
| `<DTASOF>` | Data do Saldo | `FCXA.DATASALDOBANCARIO` |

---

## 🛠️ Como Compilar

O projeto não requer instalação de Visual Studio nem de SDKs pesados. Ele pode ser compilado diretamente usando o compilador nativo do Windows (.NET Framework 4.0+):

1. Clone o repositório:
   ```bash
   git clone https://github.com/ViniMSimoes/ConvertFlow.git
   cd ConvertFlow
   ```
2. Execute o script de compilação:
   ```cmd
   build.bat
   ```
3. O executável `ConvertFlow.exe` (~45 KB) será gerado na raiz da pasta.

---

## 🚀 Como Executar

Basta dar dois cliques em:
- **`ConvertFlow.exe`**

Não é necessária nenhuma instalação prévia. O executável roda em qualquer computador com Windows 10 ou Windows 11.

---

## 📜 Licença

Este projeto está sob a licença [MIT](LICENSE).
