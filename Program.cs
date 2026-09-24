using System;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Security.Cryptography;
using System.Xml;
using System.Globalization;
using System.Data;
using System.Data.SqlClient;
using Microsoft.Win32;

namespace ConvertFlow
{
    public class App : Application
    {
        [STAThread]
        public static void Main()
        {
            App app = new App();
            MainWindow window = new MainWindow();
            app.Run(window);
        }
    }

    public class FlanResult
    {
        public string IdLan;
        public string CodTipoDoc;
        public string CodFilial;
        public string CodCfo;
        public string CodConta;
        public decimal ValorOriginal;
        public string NumeroDocumento;
        public string IdFormaPagto;
        public string Competencia;
        public bool Found;
    }

    public class CxaResult
    {
        public string CodCxa;
        public string Descricao;
        public string NomeBanco;

        public CxaResult()
        {
            CodCxa = "";
            Descricao = "";
            NomeBanco = "";
        }
    }

    public static class TotvsDbService
    {
        public static string Server = "172.20.11.108";
        public static string Database = "CORPORERM";
        public static string User = "vinicius.marcelo";
        public static string Password = "vinagre123";
        public static bool UseWindowsAuth = false;
        public static bool IsEnabled = true;

        private static Dictionary<string, FlanResult> cache = new Dictionary<string, FlanResult>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, CxaResult> cxaCache = new Dictionary<string, CxaResult>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> funcCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            lock (cache)
            {
                cache.Clear();
            }
            lock (cxaCache)
            {
                cxaCache.Clear();
            }
            lock (funcCache)
            {
                funcCache.Clear();
            }
        }

        public static string GetConnectionString()
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            builder.DataSource = Server;
            builder.InitialCatalog = Database;
            builder.ConnectTimeout = 4;
            if (UseWindowsAuth)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = User;
                builder.Password = Password;
            }
            return builder.ConnectionString;
        }

        public static string TestConnection()
        {
            try
            {
                using (SqlConnection conn = new SqlConnection(GetConnectionString()))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("SELECT TOP 1 IDLAN FROM DBO.FLAN WITH (NOLOCK)", conn))
                    {
                        cmd.CommandTimeout = 4;
                        cmd.ExecuteScalar();
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string ExtractNomeBanco(string descricao, string hint, string numBanco)
        {
            string descUpper = (descricao ?? "").ToUpperInvariant();
            if (descUpper.Contains("SICOOB")) return "SICOOB";
            if (descUpper.Contains("SANTANDER")) return "SANTANDER";
            if (descUpper.Contains("BRADESCO")) return "BRADESCO";
            if (descUpper.Contains("ITAÚ") || descUpper.Contains("ITAU")) return "ITAU";
            if (descUpper.Contains("CAIXA") || descUpper.Contains("CEF")) return "CAIXA";
            if (descUpper.Contains("BRASIL") || descUpper.Contains("BANCO DO BRASIL")) return "BANCO DO BRASIL";
            if (descUpper.Contains("SICREDI")) return "SICREDI";
            if (descUpper.Contains("SAFRA")) return "SAFRA";
            if (descUpper.Contains("DAYCOVAL")) return "DAYCOVAL";

            string hintUpper = (hint ?? "").Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(hintUpper))
            {
                if (hintUpper.Contains("SICOOB")) return "SICOOB";
                if (hintUpper.Contains("SANTANDER")) return "SANTANDER";
                if (hintUpper.Contains("BRADESCO")) return "BRADESCO";
                if (hintUpper.Contains("ITAÚ") || hintUpper.Contains("ITAU")) return "ITAU";
                if (hintUpper.Contains("CAIXA")) return "CAIXA";
                if (hintUpper.Contains("BRASIL")) return "BANCO DO BRASIL";
                if (hintUpper.Contains("SICREDI")) return "SICREDI";
                return hintUpper;
            }

            string bNoZeros = (numBanco ?? "").Trim().TrimStart('0');
            switch (bNoZeros)
            {
                case "756": return "SICOOB";
                case "33": return "SANTANDER";
                case "237": return "BRADESCO";
                case "1": return "BANCO DO BRASIL";
                case "104": return "CAIXA";
                case "341": return "ITAU";
                case "748": return "SICREDI";
                case "422": return "SAFRA";
                case "707": return "DAYCOVAL";
                default: return !string.IsNullOrEmpty(numBanco) ? ("BANCO " + numBanco) : "BANCO";
            }
        }

        public static CxaResult ResolveContaCaixa(string numBanco, string agencia, string conta, string nomeBancoHint = "")
        {
            CxaResult res = new CxaResult();
            if (!IsEnabled) return res;

            string cleanBanco = (numBanco ?? "").Trim();
            string cleanBancoNoZeros = cleanBanco.TrimStart('0');
            string cleanAg = (agencia ?? "").Trim();
            string cleanAgNoZeros = cleanAg.TrimStart('0');
            string cleanConta = (conta ?? "").Trim();
            string cleanContaNoZeros = cleanConta.TrimStart('0');
            string cleanNomeHint = (nomeBancoHint ?? "").Trim().ToUpperInvariant();

            string cacheKey = string.Format("{0}|{1}|{2}|{3}", cleanBanco, cleanAg, cleanConta, cleanNomeHint);
            lock (cxaCache)
            {
                if (cxaCache.ContainsKey(cacheKey)) return cxaCache[cacheKey];
            }

            try
            {
                using (SqlConnection conn = new SqlConnection(GetConnectionString()))
                {
                    conn.Open();

                    // 1. Tenta correspondência exata de Banco + Agência + Conta em DBO.FCXA
                    if (!string.IsNullOrEmpty(cleanBanco) && !string.IsNullOrEmpty(cleanContaNoZeros))
                    {
                        string sqlExact = @"SELECT TOP 1 CODCXA, DESCRICAO, NUMBANCO 
                                           FROM DBO.FCXA WITH (NOLOCK) 
                                           WHERE (NUMBANCO = @Banco OR LTRIM(RTRIM(NUMBANCO)) = @BancoNoZeros)
                                             AND (REPLACE(LTRIM(RTRIM(NUMAGENCIA)), '-', '') = @AgNoZeros 
                                                  OR NUMAGENCIA LIKE '%' + @AgNoZeros + '%' 
                                                  OR @AgNoZeros = '')
                                             AND (REPLACE(LTRIM(RTRIM(NROCONTA)), '-', '') = @ContaNoZeros 
                                                  OR NROCONTA LIKE '%' + @ContaNoZeros + '%')
                                           ORDER BY CASE WHEN DESCRICAO LIKE '%CONTA CAIXA%' THEN 0 ELSE 1 END, CODCXA ASC";

                        using (SqlCommand cmd = new SqlCommand(sqlExact, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Banco", cleanBanco);
                            cmd.Parameters.AddWithValue("@BancoNoZeros", cleanBancoNoZeros.Length > 0 ? cleanBancoNoZeros : cleanBanco);
                            cmd.Parameters.AddWithValue("@AgNoZeros", cleanAgNoZeros);
                            cmd.Parameters.AddWithValue("@ContaNoZeros", cleanContaNoZeros);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    res.CodCxa = reader["CODCXA"] != DBNull.Value ? reader["CODCXA"].ToString().Trim() : "";
                                    res.Descricao = reader["DESCRICAO"] != DBNull.Value ? reader["DESCRICAO"].ToString().Trim() : "";
                                    res.NomeBanco = ExtractNomeBanco(res.Descricao, cleanNomeHint, cleanBanco);
                                }
                            }
                        }
                    }

                    // 2. Se não encontrou pela conta exata, busca apenas por Banco ou Nome em FCXA
                    if (string.IsNullOrEmpty(res.CodCxa) && (!string.IsNullOrEmpty(cleanBanco) || !string.IsNullOrEmpty(cleanNomeHint)))
                    {
                        string sqlBank = @"SELECT TOP 1 CODCXA, DESCRICAO, NUMBANCO 
                                          FROM DBO.FCXA WITH (NOLOCK) 
                                          WHERE (NUMBANCO = @Banco OR LTRIM(RTRIM(NUMBANCO)) = @BancoNoZeros)
                                             OR (@NomeHint <> '' AND UPPER(DESCRICAO) LIKE '%' + @NomeHint + '%')
                                          ORDER BY CASE WHEN DESCRICAO LIKE '%CONTA CAIXA%' THEN 0 ELSE 1 END, CODCXA ASC";

                        using (SqlCommand cmd = new SqlCommand(sqlBank, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Banco", cleanBanco);
                            cmd.Parameters.AddWithValue("@BancoNoZeros", cleanBancoNoZeros.Length > 0 ? cleanBancoNoZeros : cleanBanco);
                            cmd.Parameters.AddWithValue("@NomeHint", cleanNomeHint);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    res.CodCxa = reader["CODCXA"] != DBNull.Value ? reader["CODCXA"].ToString().Trim() : "";
                                    res.Descricao = reader["DESCRICAO"] != DBNull.Value ? reader["DESCRICAO"].ToString().Trim() : "";
                                    res.NomeBanco = ExtractNomeBanco(res.Descricao, cleanNomeHint, cleanBanco);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silencioso
            }

            if (string.IsNullOrEmpty(res.NomeBanco))
            {
                res.NomeBanco = ExtractNomeBanco("", cleanNomeHint, cleanBanco);
            }

            lock (cxaCache)
            {
                cxaCache[cacheKey] = res;
            }
            return res;
        }

        public static FlanResult QueryLancamento(string docNum, string cpfCnpj, string favorecido, decimal valor)
        {
            FlanResult res = new FlanResult();
            res.Found = false;

            if (!IsEnabled) return res;

            string cleanDoc = (docNum ?? "").Trim();
            string cleanDocNoZeros = cleanDoc.TrimStart('0');
            string cleanCpf = (cpfCnpj ?? "").Replace(".", "").Replace("-", "").Replace("/", "").Trim();
            string cleanCpfNoZeros = cleanCpf.TrimStart('0');
            string cleanFav = (favorecido ?? "").Trim();

            string cacheKey = string.Format("{0}|{1}|{2}|{3}", cleanDoc, cleanCpf, cleanFav, valor);
            lock (cache)
            {
                if (cache.ContainsKey(cacheKey)) return cache[cacheKey];
            }

            try
            {
                using (SqlConnection conn = new SqlConnection(GetConnectionString()))
                {
                    conn.Open();

                    string resolvedCfo = null;
                    if (cleanCpf.Length >= 2 && (cleanCpf.StartsWith("F") || cleanCpf.StartsWith("C") || cleanCpf.StartsWith("A") || cleanCpf.StartsWith("L") || cleanCpf.StartsWith("M")))
                    {
                        resolvedCfo = cleanCpf;
                    }
                    else if (cleanCpf.Length >= 11)
                    {
                        string sqlCfo = @"SELECT TOP 1 CODCFO 
                                          FROM DBO.FCFO WITH (NOLOCK) 
                                          WHERE REPLACE(REPLACE(REPLACE(CGCCFO, '.', ''), '-', ''), '/', '') = @Cpf
                                             OR REPLACE(REPLACE(REPLACE(CGCCFO, '.', ''), '-', ''), '/', '') = @CpfNoZeros";

                        using (SqlCommand cmd = new SqlCommand(sqlCfo, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Cpf", cleanCpf);
                            cmd.Parameters.AddWithValue("@CpfNoZeros", cleanCpfNoZeros.Length > 0 ? cleanCpfNoZeros : cleanCpf);
                            object obj = cmd.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                resolvedCfo = obj.ToString().Trim();
                            }
                        }
                    }

                    // Se não encontrou por CPF e tem favorecido, busca por Nome em FCFO
                    if (string.IsNullOrEmpty(resolvedCfo) && cleanFav.Length >= 4)
                    {
                        string favPrefix = cleanFav.Substring(0, Math.Min(15, cleanFav.Length)).Trim();
                        string sqlFav = @"SELECT TOP 1 CODCFO 
                                          FROM DBO.FCFO WITH (NOLOCK) 
                                          WHERE NOME LIKE @FavPrefix + '%'";

                        using (SqlCommand cmd = new SqlCommand(sqlFav, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@FavPrefix", favPrefix);
                            object obj = cmd.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                resolvedCfo = obj.ToString().Trim();
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(resolvedCfo))
                    {
                        res.CodCfo = resolvedCfo;
                    }

                    // 1. Se tem resolvedCfo e documento, busca com máxima precisão no cliente/fornecedor (Prioriza STATUSLAN = 0)
                    if (!string.IsNullOrEmpty(resolvedCfo) && !string.IsNullOrEmpty(cleanDoc))
                    {
                        string docTail = (cleanDocNoZeros.Length >= 6) ? cleanDocNoZeros.Substring(cleanDocNoZeros.Length - 6) : cleanDocNoZeros;
                        string sqlDocCfo = @"SELECT TOP 1 IDLAN, CODTDO, CODFILIAL, CODCFO, CODCXA, VALORORIGINAL, NUMERODOCUMENTO, IDFORMAPAGTO 
                                             FROM DBO.FLAN WITH (NOLOCK) 
                                             WHERE CODCFO = @Cfo
                                               AND (STATUSLAN = 0 OR STATUSLAN = 1)
                                               AND (NUMERODOCUMENTO = @Doc 
                                                    OR RTRIM(LTRIM(NUMERODOCUMENTO)) = @DocClean
                                                    OR IPTE = @Doc
                                                    OR SEGUNDONUMERO = @Doc
                                                    OR CAST(IDLAN AS VARCHAR(20)) = @DocClean
                                                    OR CAST(IDLAN AS VARCHAR(20)) = @DocTail)
                                             ORDER BY STATUSLAN ASC, IDLAN DESC";

                        using (SqlCommand cmd = new SqlCommand(sqlDocCfo, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Cfo", resolvedCfo);
                            cmd.Parameters.AddWithValue("@Doc", cleanDoc);
                            cmd.Parameters.AddWithValue("@DocClean", cleanDocNoZeros.Length > 0 ? cleanDocNoZeros : cleanDoc);
                            cmd.Parameters.AddWithValue("@DocTail", docTail.Length > 0 ? docTail : cleanDoc);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    res.Found = true;
                                    res.IdLan = reader["IDLAN"] != DBNull.Value ? reader["IDLAN"].ToString().Trim() : "";
                                    res.CodTipoDoc = reader["CODTDO"] != DBNull.Value ? reader["CODTDO"].ToString().Trim() : "";
                                    res.CodFilial = reader["CODFILIAL"] != DBNull.Value ? reader["CODFILIAL"].ToString().Trim() : "";
                                    res.CodCfo = reader["CODCFO"] != DBNull.Value ? reader["CODCFO"].ToString().Trim() : resolvedCfo;
                                    res.CodConta = reader["CODCXA"] != DBNull.Value ? reader["CODCXA"].ToString().Trim() : "";
                                    res.NumeroDocumento = reader["NUMERODOCUMENTO"] != DBNull.Value ? reader["NUMERODOCUMENTO"].ToString().Trim() : "";
                                    res.IdFormaPagto = reader["IDFORMAPAGTO"] != DBNull.Value ? reader["IDFORMAPAGTO"].ToString().Trim() : "";
                                    if (reader["VALORORIGINAL"] != DBNull.Value) res.ValorOriginal = Convert.ToDecimal(reader["VALORORIGINAL"]);
                                }
                            }
                        }
                    }

                    // 2. Se tem resolvedCfo e valor (e não achou por doc), busca no cliente por valor (Prioriza STATUSLAN = 0)
                    if (!res.Found && !string.IsNullOrEmpty(resolvedCfo) && valor > 0m)
                    {
                        string sqlFlanCfo = @"SELECT TOP 1 IDLAN, CODTDO, CODFILIAL, CODCFO, CODCXA, VALORORIGINAL, NUMERODOCUMENTO, IDFORMAPAGTO 
                                              FROM DBO.FLAN WITH (NOLOCK) 
                                              WHERE CODCFO = @Cfo 
                                                AND (STATUSLAN = 0 OR STATUSLAN = 1)
                                                AND ABS(VALORORIGINAL - @Valor) < 0.05
                                              ORDER BY STATUSLAN ASC, IDLAN DESC";

                        using (SqlCommand cmd = new SqlCommand(sqlFlanCfo, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Cfo", resolvedCfo);
                            cmd.Parameters.AddWithValue("@Valor", valor);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    res.Found = true;
                                    res.IdLan = reader["IDLAN"] != DBNull.Value ? reader["IDLAN"].ToString().Trim() : "";
                                    res.CodTipoDoc = reader["CODTDO"] != DBNull.Value ? reader["CODTDO"].ToString().Trim() : "";
                                    res.CodFilial = reader["CODFILIAL"] != DBNull.Value ? reader["CODFILIAL"].ToString().Trim() : "";
                                    res.CodCfo = reader["CODCFO"] != DBNull.Value ? reader["CODCFO"].ToString().Trim() : resolvedCfo;
                                    res.CodConta = reader["CODCXA"] != DBNull.Value ? reader["CODCXA"].ToString().Trim() : "";
                                    res.NumeroDocumento = reader["NUMERODOCUMENTO"] != DBNull.Value ? reader["NUMERODOCUMENTO"].ToString().Trim() : "";
                                    res.IdFormaPagto = reader["IDFORMAPAGTO"] != DBNull.Value ? reader["IDFORMAPAGTO"].ToString().Trim() : "";
                                    if (reader["VALORORIGINAL"] != DBNull.Value) res.ValorOriginal = Convert.ToDecimal(reader["VALORORIGINAL"]);
                                }
                            }
                        }
                    }

                    // 3. Busca global por Documento / IDLAN / Nosso Número (Apenas se documento for longo/específico >= 6 dígitos)
                    if (!res.Found && !string.IsNullOrEmpty(cleanDoc) && cleanDocNoZeros.Length >= 6)
                    {
                        string docTail = (cleanDocNoZeros.Length >= 6) ? cleanDocNoZeros.Substring(cleanDocNoZeros.Length - 6) : cleanDocNoZeros;
                        string sqlDoc = @"SELECT TOP 1 IDLAN, CODTDO, CODFILIAL, CODCFO, CODCXA, VALORORIGINAL, NUMERODOCUMENTO, IDFORMAPAGTO 
                                          FROM DBO.FLAN WITH (NOLOCK) 
                                          WHERE (STATUSLAN = 0 OR STATUSLAN = 1)
                                            AND (NUMERODOCUMENTO = @Doc 
                                                 OR RTRIM(LTRIM(NUMERODOCUMENTO)) = @DocClean
                                                 OR IPTE = @Doc
                                                 OR SEGUNDONUMERO = @Doc
                                                 OR CAST(IDLAN AS VARCHAR(20)) = @DocClean
                                                 OR CAST(IDLAN AS VARCHAR(20)) = @DocTail
                                                 OR (@DocClean LIKE '%' + CAST(IDLAN AS VARCHAR(20)) AND IDLAN >= 100000))
                                          ORDER BY STATUSLAN ASC, IDLAN DESC";

                        using (SqlCommand cmd = new SqlCommand(sqlDoc, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Doc", cleanDoc);
                            cmd.Parameters.AddWithValue("@DocClean", cleanDocNoZeros.Length > 0 ? cleanDocNoZeros : cleanDoc);
                            cmd.Parameters.AddWithValue("@DocTail", docTail.Length > 0 ? docTail : cleanDoc);

                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    res.Found = true;
                                    res.IdLan = reader["IDLAN"] != DBNull.Value ? reader["IDLAN"].ToString().Trim() : "";
                                    res.CodTipoDoc = reader["CODTDO"] != DBNull.Value ? reader["CODTDO"].ToString().Trim() : "";
                                    res.CodFilial = reader["CODFILIAL"] != DBNull.Value ? reader["CODFILIAL"].ToString().Trim() : "";
                                    res.CodCfo = reader["CODCFO"] != DBNull.Value ? reader["CODCFO"].ToString().Trim() : "";
                                    res.CodConta = reader["CODCXA"] != DBNull.Value ? reader["CODCXA"].ToString().Trim() : "";
                                    res.NumeroDocumento = reader["NUMERODOCUMENTO"] != DBNull.Value ? reader["NUMERODOCUMENTO"].ToString().Trim() : "";
                                    res.IdFormaPagto = reader["IDFORMAPAGTO"] != DBNull.Value ? reader["IDFORMAPAGTO"].ToString().Trim() : "";
                                    if (reader["VALORORIGINAL"] != DBNull.Value) res.ValorOriginal = Convert.ToDecimal(reader["VALORORIGINAL"]);
                                }
                            }
                        }
                    }

                    // 4. Se ainda não encontrou, tenta busca LIKE por documento (Prioriza STATUSLAN = 0)
                    if (!res.Found && cleanDocNoZeros.Length >= 6)
                    {
                        string sqlLike = @"SELECT TOP 1 IDLAN, CODTDO, CODFILIAL, CODCFO, CODCXA, VALORORIGINAL, NUMERODOCUMENTO, IDFORMAPAGTO 
                                           FROM DBO.FLAN WITH (NOLOCK) 
                                           WHERE (STATUSLAN = 0 OR STATUSLAN = 1)
                                             AND (NUMERODOCUMENTO LIKE '%' + @DocClean
                                                  OR IPTE LIKE '%' + @DocClean
                                                  OR SEGUNDONUMERO LIKE '%' + @DocClean)
                                           ORDER BY STATUSLAN ASC, IDLAN DESC";

                        using (SqlCommand cmd = new SqlCommand(sqlLike, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@DocClean", cleanDocNoZeros);
                            using (SqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    res.Found = true;
                                    res.IdLan = reader["IDLAN"] != DBNull.Value ? reader["IDLAN"].ToString().Trim() : "";
                                    res.CodTipoDoc = reader["CODTDO"] != DBNull.Value ? reader["CODTDO"].ToString().Trim() : "";
                                    res.CodFilial = reader["CODFILIAL"] != DBNull.Value ? reader["CODFILIAL"].ToString().Trim() : "";
                                    res.CodCfo = reader["CODCFO"] != DBNull.Value ? reader["CODCFO"].ToString().Trim() : "";
                                    res.CodConta = reader["CODCXA"] != DBNull.Value ? reader["CODCXA"].ToString().Trim() : "";
                                    res.NumeroDocumento = reader["NUMERODOCUMENTO"] != DBNull.Value ? reader["NUMERODOCUMENTO"].ToString().Trim() : "";
                                    res.IdFormaPagto = reader["IDFORMAPAGTO"] != DBNull.Value ? reader["IDFORMAPAGTO"].ToString().Trim() : "";
                                    if (reader["VALORORIGINAL"] != DBNull.Value) res.ValorOriginal = Convert.ToDecimal(reader["VALORORIGINAL"]);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Falha silenciosa caso o banco esteja indisponível
            }

            lock (cache)
            {
                cache[cacheKey] = res;
            }
            return res;
        }

        public static FlanResult QueryLancamento(string docNum, string cpfCnpj, decimal valor)
        {
            return QueryLancamento(docNum, cpfCnpj, "", valor);
        }

        public static string QueryFuncionarioByChapa(string docNum, string cpf = "")
        {
            if (!IsEnabled) return null;
            if (string.IsNullOrEmpty(docNum) && string.IsNullOrEmpty(cpf)) return null;

            string cleanDoc = (docNum ?? "").Trim();
            string cleanCpf = (cpf ?? "").Replace(".", "").Replace("-", "").Replace("/", "").Trim();

            string chapa = "";
            if (cleanDoc.Length == 12 && (cleanDoc.StartsWith("20") || cleanDoc.StartsWith("19")))
            {
                chapa = cleanDoc.Substring(6, 6);
            }
            else if (cleanDoc.Length >= 6 && IsAllDigits(cleanDoc))
            {
                chapa = cleanDoc.Substring(cleanDoc.Length - 6);
            }
            else if (cleanDoc.Length > 0 && cleanDoc.Length < 6 && IsAllDigits(cleanDoc))
            {
                chapa = cleanDoc.PadLeft(6, '0');
            }

            string cacheKey = string.Format("{0}|{1}", chapa, cleanCpf);
            lock (funcCache)
            {
                if (funcCache.ContainsKey(cacheKey)) return funcCache[cacheKey];
            }

            string nomeEncontrado = null;

            try
            {
                using (SqlConnection conn = new SqlConnection(GetConnectionString()))
                {
                    conn.Open();

                    // 1. Busca por CHAPA na tabela PFUNC
                    if (!string.IsNullOrEmpty(chapa))
                    {
                        string sqlChapa = "SELECT TOP 1 NOME FROM DBO.PFUNC WITH (NOLOCK) WHERE CHAPA = @Chapa";
                        using (SqlCommand cmd = new SqlCommand(sqlChapa, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Chapa", chapa);
                            object obj = cmd.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                string val = obj.ToString().Trim();
                                if (!string.IsNullOrEmpty(val)) nomeEncontrado = val;
                            }
                        }
                    }

                    // 2. Se não encontrou por CHAPA e tem CPF, busca por CPF via PPESSOA
                    if (string.IsNullOrEmpty(nomeEncontrado) && cleanCpf.Length >= 11)
                    {
                        string sqlCpf = @"SELECT TOP 1 F.NOME 
                                          FROM DBO.PFUNC F WITH (NOLOCK) 
                                          INNER JOIN DBO.PPESSOA P WITH (NOLOCK) ON F.CODPESSOA = P.CODIGO 
                                          WHERE REPLACE(REPLACE(REPLACE(P.CPF, '.', ''), '-', ''), '/', '') = @Cpf";
                        using (SqlCommand cmd = new SqlCommand(sqlCpf, conn))
                        {
                            cmd.CommandTimeout = 4;
                            cmd.Parameters.AddWithValue("@Cpf", cleanCpf);
                            object obj = cmd.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                string val = obj.ToString().Trim();
                                if (!string.IsNullOrEmpty(val)) nomeEncontrado = val;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silencioso
            }

            lock (funcCache)
            {
                funcCache[cacheKey] = nomeEncontrado;
            }

            return nomeEncontrado;
        }

        public static bool IsAllDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return true;
        }

        public static string ResolveCompetencia(string flanComp, string numDoc, string dtBaixa6)
        {
            if (!string.IsNullOrEmpty(flanComp)) return flanComp;

            string cleanDoc = (numDoc ?? "").Trim();
            if (cleanDoc.Length >= 6)
            {
                int y, m;
                if (int.TryParse(cleanDoc.Substring(0, 4), out y) && y >= 1990 && y <= 2099 &&
                    int.TryParse(cleanDoc.Substring(4, 2), out m) && m >= 1 && m <= 12)
                {
                    return string.Format("{0:D2}/{1:D4}", m, y);
                }
            }

            string dt = (dtBaixa6 ?? "").Trim();
            if (dt.Length == 6)
            {
                int m, yy;
                if (int.TryParse(dt.Substring(2, 2), out m) && m >= 1 && m <= 12 &&
                    int.TryParse(dt.Substring(4, 2), out yy))
                {
                    int yyyy = (yy >= 50) ? (1900 + yy) : (2000 + yy);
                    return string.Format("{0:D2}/{1:D4}", m, yyyy);
                }
            }

            return "";
        }
    }

    public class DbConfigWindow : Window
    {
        private TextBox txtServer;
        private TextBox txtDatabase;
        private TextBox txtUser;
        private PasswordBox txtPassword;
        private CheckBox chkWinAuth;
        private TextBlock txtStatus;
        private Button btnTest;
        private Button btnSave;

        public DbConfigWindow()
        {
            Title = "Configurações da Conexão TOTVS RM (SQL Server)";
            Width = 490;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));

            Grid grid = new Grid();
            grid.Margin = new Thickness(22);
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto });

            // Header
            StackPanel header = new StackPanel() { Margin = new Thickness(0, 0, 0, 16) };
            header.Children.Add(new TextBlock()
            {
                Text = "⚡ Conexão ao Banco de Dados TOTVS RM",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            header.Children.Add(new TextBlock()
            {
                Text = "Consulta automática do Tipo de Documento (CODTIPODOC) e IDLAN na tabela FLAN.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // Form
            StackPanel form = new StackPanel();

            form.Children.Add(new TextBlock() { Text = "Servidor / Host SQL Server:", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), Margin = new Thickness(0, 0, 0, 3) });
            txtServer = new TextBox() { Text = TotvsDbService.Server, Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(8, 5, 8, 5), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            form.Children.Add(txtServer);

            form.Children.Add(new TextBlock() { Text = "Banco de Dados (Database):", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), Margin = new Thickness(0, 0, 0, 3) });
            txtDatabase = new TextBox() { Text = TotvsDbService.Database, Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(8, 5, 8, 5), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            form.Children.Add(txtDatabase);

            chkWinAuth = new CheckBox()
            {
                Content = "Usar Autenticação do Windows (Integrated Security)",
                IsChecked = TotvsDbService.UseWindowsAuth,
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 8)
            };
            form.Children.Add(chkWinAuth);

            TextBlock lblUser = new TextBlock() { Text = "Usuário SQL Server:", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), Margin = new Thickness(0, 0, 0, 3) };
            txtUser = new TextBox() { Text = TotvsDbService.User, Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(8, 5, 8, 5), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            form.Children.Add(lblUser);
            form.Children.Add(txtUser);

            TextBlock lblPass = new TextBlock() { Text = "Senha:", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)), Margin = new Thickness(0, 0, 0, 3) };
            txtPassword = new PasswordBox() { Password = TotvsDbService.Password, Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(8, 5, 8, 5), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            form.Children.Add(lblPass);
            form.Children.Add(txtPassword);

            chkWinAuth.Checked += (s, e) => { txtUser.IsEnabled = false; txtPassword.IsEnabled = false; };
            chkWinAuth.Unchecked += (s, e) => { txtUser.IsEnabled = true; txtPassword.IsEnabled = true; };
            if (chkWinAuth.IsChecked == true) { txtUser.IsEnabled = false; txtPassword.IsEnabled = false; }

            Grid.SetRow(form, 1);
            grid.Children.Add(form);

            // Status Text
            txtStatus = new TextBlock()
            {
                Text = "",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10)
            };
            Grid.SetRow(txtStatus, 2);
            grid.Children.Add(txtStatus);

            // Buttons
            Grid btnGrid = new Grid();
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            btnTest = new Button()
            {
                Content = "🔌 Testar Conexão",
                Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 12, 7),
                FontSize = 11,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btnTest.Click += BtnTest_Click;
            Grid.SetColumn(btnTest, 0);
            btnGrid.Children.Add(btnTest);

            Button btnCancel = new Button()
            {
                Content = "Cancelar",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 7, 12, 7),
                FontSize = 11,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            Grid.SetColumn(btnCancel, 1);
            btnGrid.Children.Add(btnCancel);

            btnSave = new Button()
            {
                Content = "Salvar",
                Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Foreground = new SolidColorBrush(Color.FromRgb(9, 9, 11)),
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 7, 16, 7),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            btnSave.Click += BtnSave_Click;
            Grid.SetColumn(btnSave, 2);
            btnGrid.Children.Add(btnSave);

            Grid.SetRow(btnGrid, 3);
            grid.Children.Add(btnGrid);

            Content = grid;
        }

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            btnTest.IsEnabled = false;
            txtStatus.Text = "Conectando ao SQL Server " + txtServer.Text.Trim() + "...";
            txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216));

            string prevServer = TotvsDbService.Server;
            string prevDb = TotvsDbService.Database;
            string prevUser = TotvsDbService.User;
            string prevPass = TotvsDbService.Password;
            bool prevWin = TotvsDbService.UseWindowsAuth;

            TotvsDbService.Server = txtServer.Text.Trim();
            TotvsDbService.Database = txtDatabase.Text.Trim();
            TotvsDbService.User = txtUser.Text.Trim();
            TotvsDbService.Password = txtPassword.Password;
            TotvsDbService.UseWindowsAuth = (chkWinAuth.IsChecked == true);

            string err = TotvsDbService.TestConnection();

            if (err == null)
            {
                txtStatus.Text = "✅ Conexão estabelecida com sucesso! Tabela FLAN acessível.";
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else
            {
                txtStatus.Text = "❌ Falha na conexão: " + err;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                TotvsDbService.Server = prevServer;
                TotvsDbService.Database = prevDb;
                TotvsDbService.User = prevUser;
                TotvsDbService.Password = prevPass;
                TotvsDbService.UseWindowsAuth = prevWin;
            }
            btnTest.IsEnabled = true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            TotvsDbService.Server = txtServer.Text.Trim();
            TotvsDbService.Database = txtDatabase.Text.Trim();
            TotvsDbService.User = txtUser.Text.Trim();
            TotvsDbService.Password = txtPassword.Password;
            TotvsDbService.UseWindowsAuth = (chkWinAuth.IsChecked == true);
            TotvsDbService.ClearCache();
            DialogResult = true;
            Close();
        }
    }

    public class MainWindow : Window
    {
        private string selectedFilePath = null;
        private string detectedExtension = null;
        private string targetFormat = "OFX";
        private string documentsDir;
        private string configPath;

        // UI Controls
        private Border dropZone;
        private StackPanel fileInfoPanel;
        private TextBlock fileNameText;
        private TextBlock fileMetaText;
        private StackPanel formatOptionsPanel;
        private Border totvsConfigBorder;
        private TextBox txtBanco;
        private TextBox txtAgencia;
        private TextBox txtConta;
        private Border baixaConfigBorder;
        private TextBlock dbStatusText;
        private string currentFilial = "0002";
        private string currentTipoDoc = "SALP";
        private string currentContaCaixa = "30";
        private string currentFormaPgto = "3";
        private Button convertButton;
        private Border resultPanel;
        private TextBlock resultPathText;
        private List<RadioButton> formatRadios = new List<RadioButton>();
        private string lastSavedPath = null;

        public MainWindow()
        {
            Title = "ConvertFlow - Conversor de Arquivos (Padrão TOTVS RM)";
            Width = 640;
            Height = 630;
            MinWidth = 550;
            MinHeight = 550;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)); // #0e0e11

            documentsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Arquivos_Convertidos");
            if (!Directory.Exists(documentsDir)) Directory.CreateDirectory(documentsDir);

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConvertFlow");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            configPath = Path.Combine(appData, "totvs_config.ini");

            BuildUI();
            LoadTotvsConfig();
        }

        private void BuildUI()
        {
            Grid mainGrid = new Grid();
            mainGrid.Margin = new Thickness(24);
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); // Content
            mainGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // Footer

            // --- 1. HEADER ---
            Grid header = new Grid();
            header.Margin = new Thickness(0, 0, 0, 18);
            header.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            StackPanel titlePanel = new StackPanel();
            TextBlock title = new TextBlock()
            {
                Text = "ConvertFlow",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            TextBlock subtitle = new TextBlock()
            {
                Text = "Software Nativo de Conversão • Padrão TOTVS Gestão Financeira",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            titlePanel.Children.Add(title);
            titlePanel.Children.Add(subtitle);
            Grid.SetColumn(titlePanel, 0);
            header.Children.Add(titlePanel);

            Button docsBtn = new Button()
            {
                Content = "📂 Ver Documentos",
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
                Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            docsBtn.Click += (s, e) => Process.Start("explorer.exe", documentsDir);
            Grid.SetColumn(docsBtn, 1);
            header.Children.Add(docsBtn);

            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // --- 2. CARD PRINCIPAL ---
            Border card = new Border()
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20)
            };

            StackPanel cardContent = new StackPanel();

            // DROPZONE
            dropZone = new Border()
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 21)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 35, 20, 35),
                AllowDrop = true,
                Cursor = Cursors.Hand
            };

            dropZone.DragOver += DropZone_DragOver;
            dropZone.DragLeave += DropZone_DragLeave;
            dropZone.Drop += DropZone_Drop;
            dropZone.MouseLeftButtonDown += (s, e) => OpenFileDialogPrompt();

            StackPanel dropInner = new StackPanel() { HorizontalAlignment = HorizontalAlignment.Center };
            TextBlock dropIcon = new TextBlock()
            {
                Text = "📥",
                FontSize = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            TextBlock dropTitle = new TextBlock()
            {
                Text = "Arraste o arquivo aqui ou clique para selecionar",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            TextBlock dropSub = new TextBlock()
            {
                Text = "Suporta Retorno CNAB (TXT/RET), CSV, OFX, Excel (.xlsx/.xls)",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            dropInner.Children.Add(dropIcon);
            dropInner.Children.Add(dropTitle);
            dropInner.Children.Add(dropSub);
            dropZone.Child = dropInner;
            cardContent.Children.Add(dropZone);

            // FILE INFO PANEL
            fileInfoPanel = new StackPanel() { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
            
            Border fileInfoBox = new Border()
            {
                Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12)
            };

            Grid fileInfoGrid = new Grid();
            fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            fileInfoGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            StackPanel fileTextStack = new StackPanel();
            fileNameText = new TextBlock() { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            fileMetaText = new TextBlock() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)), Margin = new Thickness(0, 2, 0, 0) };
            fileTextStack.Children.Add(fileNameText);
            fileTextStack.Children.Add(fileMetaText);
            Grid.SetColumn(fileTextStack, 0);
            fileInfoGrid.Children.Add(fileTextStack);

            Button changeBtn = new Button()
            {
                Content = "Trocar",
                Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            changeBtn.Click += (s, e) => ResetFileSelection();
            Grid.SetColumn(changeBtn, 1);
            fileInfoGrid.Children.Add(changeBtn);

            fileInfoBox.Child = fileInfoGrid;
            fileInfoPanel.Children.Add(fileInfoBox);

            // FORMAT OPTIONS
            TextBlock formatLabel = new TextBlock()
            {
                Text = "CONVERTER PARA:",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
                Margin = new Thickness(0, 12, 0, 8)
            };
            fileInfoPanel.Children.Add(formatLabel);

            formatOptionsPanel = new StackPanel() { Orientation = Orientation.Horizontal };
            fileInfoPanel.Children.Add(formatOptionsPanel);

            // CONFIGURAÇÃO CONTA CAIXA TOTVS (Aparece quando OFX está selecionado)
            totvsConfigBorder = new Border()
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Visible
            };

            StackPanel totvsStack = new StackPanel();
            TextBlock totvsTitle = new TextBlock()
            {
                Text = "Conta Bancária para Conciliação TOTVS (FCXA):",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            totvsStack.Children.Add(totvsTitle);

            Grid bankGrid = new Grid();
            bankGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            bankGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            bankGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1.4, GridUnitType.Star) });

            // Campo Banco
            StackPanel bCol = new StackPanel() { Margin = new Thickness(0, 0, 6, 0) };
            bCol.Children.Add(new TextBlock() { Text = "Banco (NUMBANCO)", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)), Margin = new Thickness(0, 0, 0, 2) });
            txtBanco = new TextBox() { Text = "001", Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(6, 4, 6, 4), FontSize = 11 };
            bCol.Children.Add(txtBanco);
            Grid.SetColumn(bCol, 0);
            bankGrid.Children.Add(bCol);

            // Campo Agencia
            StackPanel agCol = new StackPanel() { Margin = new Thickness(6, 0, 6, 0) };
            agCol.Children.Add(new TextBlock() { Text = "Agência (CODAGENCIA)", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)), Margin = new Thickness(0, 0, 0, 2) });
            txtAgencia = new TextBox() { Text = "0001", Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(6, 4, 6, 4), FontSize = 11 };
            agCol.Children.Add(txtAgencia);
            Grid.SetColumn(agCol, 1);
            bankGrid.Children.Add(agCol);

            // Campo Conta
            StackPanel cCol = new StackPanel() { Margin = new Thickness(6, 0, 0, 0) };
            cCol.Children.Add(new TextBlock() { Text = "Conta (CODCONTA)", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)), Margin = new Thickness(0, 0, 0, 2) });
            txtConta = new TextBox() { Text = "123456", Background = new SolidColorBrush(Color.FromRgb(14, 14, 17)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)), Padding = new Thickness(6, 4, 6, 4), FontSize = 11 };
            cCol.Children.Add(txtConta);
            Grid.SetColumn(cCol, 2);
            bankGrid.Children.Add(cCol);

            totvsStack.Children.Add(bankGrid);
            totvsConfigBorder.Child = totvsStack;
            fileInfoPanel.Children.Add(totvsConfigBorder);

            // CONFIGURAÇÃO BAIXA TOTVS RM (Aparece quando BAIXA está selecionado)
            baixaConfigBorder = new Border()
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Collapsed
            };

            StackPanel baixaStack = new StackPanel();

            Grid baixaHeaderGrid = new Grid();
            baixaHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            baixaHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });

            StackPanel titleBox = new StackPanel();
            TextBlock baixaTitle = new TextBlock()
            {
                Text = "⚡ Consulta Automática ao Banco TOTVS RM",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129))
            };
            TextBlock baixaSubtitle = new TextBlock()
            {
                Text = "Tipo de Documento (CODTIPODOC), IDLAN, Filial e Conta são preenchidos 100% automático via banco de dados e arquivo.",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            titleBox.Children.Add(baixaTitle);
            titleBox.Children.Add(baixaSubtitle);
            Grid.SetColumn(titleBox, 0);
            baixaHeaderGrid.Children.Add(titleBox);

            Button btnDbConfig = new Button()
            {
                Content = "⚙️ Conexão Banco",
                Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnDbConfig.Click += (s, e) =>
            {
                DbConfigWindow dlg = new DbConfigWindow();
                dlg.Owner = this;
                if (dlg.ShowDialog() == true)
                {
                    SaveTotvsConfig();
                    UpdateDbStatusLabel();
                }
            };
            Grid.SetColumn(btnDbConfig, 1);
            baixaHeaderGrid.Children.Add(btnDbConfig);
            baixaStack.Children.Add(baixaHeaderGrid);

            dbStatusText = new TextBlock()
            {
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
                Margin = new Thickness(0, 8, 0, 0)
            };
            baixaStack.Children.Add(dbStatusText);

            baixaConfigBorder.Child = baixaStack;
            fileInfoPanel.Children.Add(baixaConfigBorder);

            // BOTAO CONVERTER
            convertButton = new Button()
            {
                Content = "CONVERTER ARQUIVO",
                Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Foreground = new SolidColorBrush(Color.FromRgb(9, 9, 11)),
                FontWeight = FontWeights.ExtraBold,
                FontSize = 14,
                Padding = new Thickness(0, 13, 0, 13),
                Margin = new Thickness(0, 14, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            convertButton.Click += ConvertButton_Click;
            fileInfoPanel.Children.Add(convertButton);

            cardContent.Children.Add(fileInfoPanel);

            // RESULT PANEL
            resultPanel = new Border()
            {
                Background = new SolidColorBrush(Color.FromRgb(6, 78, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 16, 0, 0),
                Visibility = Visibility.Collapsed
            };

            StackPanel resultStack = new StackPanel();
            TextBlock successTitle = new TextBlock()
            {
                Text = "✅ Arquivo convertido e pronto para o TOTVS!",
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                FontSize = 13
            };
            resultPathText = new TextBlock()
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 250, 229)),
                Margin = new Thickness(0, 4, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };

            WrapPanel resultActions = new WrapPanel();
            Button openFolderBtn = new Button()
            {
                Content = "📂 Abrir na Pasta",
                Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Foreground = new SolidColorBrush(Color.FromRgb(9, 9, 11)),
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            openFolderBtn.Click += (s, e) =>
            {
                if (File.Exists(lastSavedPath))
                    Process.Start("explorer.exe", string.Format("/select,\"{0}\"", lastSavedPath));
                else
                    Process.Start("explorer.exe", documentsDir);
            };

            Button openFileBtn = new Button()
            {
                Content = "📄 Abrir Arquivo",
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            openFileBtn.Click += (s, e) =>
            {
                if (File.Exists(lastSavedPath)) Process.Start(lastSavedPath);
            };

            Button newFileBtn = new Button()
            {
                Content = "Converter Outro",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(209, 250, 229)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            newFileBtn.Click += (s, e) => ResetFileSelection();

            resultActions.Children.Add(openFolderBtn);
            resultActions.Children.Add(openFileBtn);
            resultActions.Children.Add(newFileBtn);

            resultStack.Children.Add(successTitle);
            resultStack.Children.Add(resultPathText);
            resultStack.Children.Add(resultActions);
            resultPanel.Child = resultStack;

            cardContent.Children.Add(resultPanel);

            card.Child = cardContent;
            Grid.SetRow(card, 1);
            mainGrid.Children.Add(card);

            // --- 3. FOOTER ---
            TextBlock footer = new TextBlock()
            {
                Text = "Salvo em: Documentos\\Arquivos_Convertidos • Compatível com TOTVS RM e Protheus",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(footer, 2);
            mainGrid.Children.Add(footer);

            Content = mainGrid;
        }

        private void LoadTotvsConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string[] lines = File.ReadAllLines(configPath);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("BANCO=")) txtBanco.Text = line.Substring(6).Trim();
                        else if (line.StartsWith("AGENCIA=")) txtAgencia.Text = line.Substring(8).Trim();
                        else if (line.StartsWith("CONTA=")) txtConta.Text = line.Substring(6).Trim();
                        else if (line.StartsWith("FILIAL=")) currentFilial = line.Substring(7).Trim();
                        else if (line.StartsWith("TIPODOC="))
                        {
                            string td = line.Substring(8).Trim();
                            if (td == "ICOP" || td == "CRMD" || string.IsNullOrEmpty(td)) td = "SALP";
                            currentTipoDoc = td;
                        }
                        else if (line.StartsWith("CONTACAIXA="))
                        {
                            string cc = line.Substring(11).Trim();
                            if (cc == "1" || string.IsNullOrEmpty(cc)) cc = "30";
                            currentContaCaixa = cc;
                        }
                        else if (line.StartsWith("FORMAPGTO="))
                        {
                            string fp = line.Substring(10).Trim();
                            if (fp == "12" || string.IsNullOrEmpty(fp)) fp = "3";
                            currentFormaPgto = CnabToBaixaConverter.NormalizeIdFormaPgto(fp);
                        }
                        else if (line.StartsWith("DB_SERVER="))
                        {
                            string s = line.Substring(10).Trim();
                            if (s == "172.20.11.113") s = "172.20.11.108";
                            TotvsDbService.Server = s;
                        }
                        else if (line.StartsWith("DB_DATABASE=")) TotvsDbService.Database = line.Substring(12).Trim();
                        else if (line.StartsWith("DB_USER=")) TotvsDbService.User = line.Substring(8).Trim();
                        else if (line.StartsWith("DB_PASSWORD="))
                        {
                            string raw = line.Substring(12).Trim();
                            try
                            {
                                byte[] bytes = Convert.FromBase64String(raw);
                                TotvsDbService.Password = Encoding.UTF8.GetString(bytes);
                            }
                            catch
                            {
                                TotvsDbService.Password = raw;
                            }
                        }
                        else if (line.StartsWith("DB_WINAUTH=")) TotvsDbService.UseWindowsAuth = (line.Substring(11).Trim() == "1");
                        else if (line.StartsWith("DB_ENABLED=")) TotvsDbService.IsEnabled = (line.Substring(11).Trim() != "0");
                    }
                    if (string.IsNullOrEmpty(TotvsDbService.Password)) TotvsDbService.Password = "vinagre123";
                }
                UpdateDbStatusLabel();
            }
            catch { }
        }

        private void SaveTotvsConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("BANCO=" + txtBanco.Text.Trim());
                sb.AppendLine("AGENCIA=" + txtAgencia.Text.Trim());
                sb.AppendLine("CONTA=" + txtConta.Text.Trim());
                sb.AppendLine("FILIAL=" + (currentFilial ?? "0002"));
                sb.AppendLine("TIPODOC=" + (currentTipoDoc ?? "SALP"));
                sb.AppendLine("CONTACAIXA=" + (currentContaCaixa ?? "30"));
                sb.AppendLine("FORMAPGTO=" + CnabToBaixaConverter.NormalizeIdFormaPgto(currentFormaPgto ?? "3"));
                sb.AppendLine("DB_SERVER=" + (TotvsDbService.Server ?? "172.20.11.108"));
                sb.AppendLine("DB_DATABASE=" + (TotvsDbService.Database ?? "CORPORERM"));
                sb.AppendLine("DB_USER=" + (TotvsDbService.User ?? "vinicius.marcelo"));
                string passB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(TotvsDbService.Password ?? ""));
                sb.AppendLine("DB_PASSWORD=" + passB64);
                sb.AppendLine("DB_WINAUTH=" + (TotvsDbService.UseWindowsAuth ? "1" : "0"));
                sb.AppendLine("DB_ENABLED=" + (TotvsDbService.IsEnabled ? "1" : "0"));
                File.WriteAllText(configPath, sb.ToString());
            }
            catch { }
        }

        private void UpdateDbStatusLabel()
        {
            if (dbStatusText == null) return;
            if (!TotvsDbService.IsEnabled)
            {
                dbStatusText.Text = "⚪ Consulta ao banco desativada (usando dados automáticos do arquivo)";
                dbStatusText.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170));
            }
            else if (string.IsNullOrEmpty(TotvsDbService.Password) && !TotvsDbService.UseWindowsAuth)
            {
                dbStatusText.Text = "⚠️ Servidor " + TotvsDbService.Server + " configurado. Clique em '⚙️ Conexão Banco' para autenticar.";
                dbStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            }
            else
            {
                dbStatusText.Text = "🟢 Conectado ao TOTVS RM (" + TotvsDbService.Server + " / " + TotvsDbService.Database + ") - Consulta FLAN ativa";
                dbStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                dropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                dropZone.Background = new SolidColorBrush(Color.FromRgb(20, 30, 25));
            }
            e.Handled = true;
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            dropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70));
            dropZone.Background = new SolidColorBrush(Color.FromRgb(18, 18, 21));
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            DropZone_DragLeave(sender, e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    LoadSelectedFile(files[0]);
                }
            }
        }

        private void OpenFileDialogPrompt()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Arquivos Suportados (*.txt;*.ret;*.rem;*.csv;*.ofx;*.xlsx;*.xls)|*.txt;*.ret;*.rem;*.csv;*.ofx;*.xlsx;*.xls|Arquivos de Retorno CNAB / Texto (*.txt;*.ret;*.rem)|*.txt;*.ret;*.rem|Planilhas CSV (*.csv)|*.csv|Extratos OFX (*.ofx)|*.ofx|Planilhas Excel (*.xlsx;*.xls)|*.xlsx;*.xls|Todos os Arquivos (*.*)|*.*";
            if (dlg.ShowDialog() == true)
            {
                LoadSelectedFile(dlg.FileName);
            }
        }

        private void LoadSelectedFile(string path)
        {
            if (!File.Exists(path)) return;

            selectedFilePath = path;
            detectedExtension = Path.GetExtension(path).ToLower().Replace(".", "");
            FileInfo info = new FileInfo(path);

            fileNameText.Text = info.Name;
            fileMetaText.Text = string.Format("{0:0.0} KB • Formato detectado: {1}", info.Length / 1024.0, detectedExtension.ToUpper());

            AutoDetectParameters(path);

            formatOptionsPanel.Children.Clear();
            formatRadios.Clear();

            List<string> options = new List<string>();
            bool isBaixaRm = false;
            if (detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem")
            {
                try
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(path);
                    string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> lines = new List<string>();
                    foreach (string l in rawLines) { string t = l.TrimEnd(); if (!string.IsNullOrEmpty(t)) lines.Add(t); }
                    if (CnabToBaixaConverter.IsBaixaRmLayout(lines)) isBaixaRm = true;
                }
                catch { }

                if (isBaixaRm)
                {
                    options.Add("BAIXA");
                    options.Add("XLSX");
                    options.Add("CSV");
                    options.Add("TXT");
                    options.Add("PDF");
                }
                else
                {
                    options.Add("BAIXA");
                    options.Add("OFX");
                    options.Add("XLSX");
                    options.Add("CSV");
                }
            }
            else if (detectedExtension == "csv")
            {
                options.Add("OFX");
                options.Add("BAIXA");
                options.Add("XLSX");
                options.Add("PDF");
                options.Add("TXT");
            }
            else if (detectedExtension == "ofx")
            {
                options.Add("CSV");
                options.Add("XLSX");
                options.Add("PDF");
                options.Add("TXT");
            }
            else if (detectedExtension == "xlsx" || detectedExtension == "xls")
            {
                options.Add("OFX");
                options.Add("BAIXA");
                options.Add("CSV");
                options.Add("PDF");
                options.Add("TXT");
            }
            else
            {
                options.Add("BAIXA");
                options.Add("OFX");
                options.Add("XLSX");
                options.Add("PDF");
                options.Add("TXT");
            }

            targetFormat = options[0];

            for (int i = 0; i < options.Count; i++)
            {
                string fmt = options[i];
                string desc = (fmt == "BAIXA" ? " (Baixa TOTVS RM)" :
                               fmt == "OFX" ? " (Extrato TOTVS)" :
                               fmt == "XLSX" ? " (Excel)" :
                               fmt == "PDF" ? " (Documento)" :
                               fmt == "CSV" ? " (Planilha CSV)" :
                               fmt == "TXT" ? " (Texto)" : "");
                RadioButton rb = new RadioButton()
                {
                    Content = fmt + desc,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 16, 0),
                    IsChecked = (i == 0),
                    Tag = fmt,
                    Cursor = Cursors.Hand
                };
                rb.Checked += (s, e) =>
                {
                    targetFormat = (string)((RadioButton)s).Tag;
                    totvsConfigBorder.Visibility = (targetFormat == "OFX") ? Visibility.Visible : Visibility.Collapsed;
                    baixaConfigBorder.Visibility = (targetFormat == "BAIXA") ? Visibility.Visible : Visibility.Collapsed;
                };
                formatRadios.Add(rb);
                formatOptionsPanel.Children.Add(rb);
            }

            totvsConfigBorder.Visibility = (targetFormat == "OFX") ? Visibility.Visible : Visibility.Collapsed;
            baixaConfigBorder.Visibility = (targetFormat == "BAIXA") ? Visibility.Visible : Visibility.Collapsed;
            dropZone.Visibility = Visibility.Collapsed;
            fileInfoPanel.Visibility = Visibility.Visible;
            resultPanel.Visibility = Visibility.Collapsed;
        }

        private void AutoDetectParameters(string path)
        {
            try
            {
                if (detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem")
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(path);
                    string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> lines = new List<string>();
                    foreach (string l in rawLines)
                    {
                        string t = l.TrimEnd();
                        if (!string.IsNullOrEmpty(t)) lines.Add(t);
                    }

                    if (lines.Count > 0)
                    {
                        if (CnabToBaixaConverter.IsBaixaRmLayout(lines))
                        {
                            foreach (string l in lines)
                            {
                                if (l.Length >= 900 && l.StartsWith("L"))
                                {
                                    string fil = l.Substring(1, 4).Trim();
                                    string td = l.Substring(30, 10).Trim();
                                    string cc = l.Substring(144, 10).Trim();
                                    string fp = (l.Length >= 937) ? l.Substring(930, 7).Trim() : (l.Length >= 931 ? l.Substring(930).Trim() : "");

                                    if (!string.IsNullOrEmpty(fil)) currentFilial = fil;
                                    if (!string.IsNullOrEmpty(td)) currentTipoDoc = td;
                                    if (!string.IsNullOrEmpty(cc)) currentContaCaixa = cc;
                                    if (!string.IsNullOrEmpty(fp)) currentFormaPgto = fp;
                                    break;
                                }
                            }
                            return;
                        }

                        // CNAB 240 ou CNAB 400
                        foreach (string l in lines)
                        {
                            if (l.Length >= 14 && l.Substring(7, 1) == "0") // Header de Arquivo CNAB 240
                            {
                                string cnabBanco = l.Substring(0, 3).Trim();
                                string cnabAg = l.Length >= 57 ? l.Substring(52, 5).Trim() : "";
                                string cnabConta = l.Length >= 70 ? l.Substring(58, 12).Trim() : "";
                                string cnabNome = l.Length >= 132 ? l.Substring(102, 30).Trim() : "";
                                var cxa = TotvsDbService.ResolveContaCaixa(cnabBanco, cnabAg, cnabConta, cnabNome);
                                if (!string.IsNullOrEmpty(cxa.CodCxa))
                                {
                                    currentContaCaixa = cxa.CodCxa;
                                }
                                currentFormaPgto = "3";
                                currentTipoDoc = "SALP";
                                break;
                            }
                            else if (l.Length >= 400 && l.StartsWith("0")) // Header CNAB 400
                            {
                                string cnabBanco = l.Length >= 79 ? l.Substring(76, 3).Trim() : "";
                                string cnabAg = l.Length >= 30 ? l.Substring(26, 4).Trim() : "";
                                string cnabConta = l.Length >= 39 ? l.Substring(31, 8).Trim() : "";
                                string cnabNome = l.Length >= 94 ? l.Substring(79, 15).Trim() : "";
                                var cxa = TotvsDbService.ResolveContaCaixa(cnabBanco, cnabAg, cnabConta, cnabNome);
                                if (!string.IsNullOrEmpty(cxa.CodCxa))
                                {
                                    currentContaCaixa = cxa.CodCxa;
                                }
                                currentFormaPgto = "3";
                                currentTipoDoc = "SALP";
                                break;
                            }
                            else if (l.Length >= 240 && l.Length > 13 && l.Substring(7, 1) == "1")
                            {
                                currentFormaPgto = "3";
                                currentTipoDoc = "SALP";
                                break;
                            }
                        }
                    }
                }
                else if (detectedExtension == "xlsx" || detectedExtension == "xls")
                {
                    var rows = SimpleXlsxReader.ReadRowsFromXlsx(path);
                    DetectTableParameters(rows);
                }
                else if (detectedExtension == "csv")
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(path);
                    char delim = CsvToOfxConverter.DetectDelimiter(rawText);
                    var rows = CsvToOfxConverter.ParseCsv(rawText, delim);
                    DetectTableParameters(rows);
                }
            }
            catch { }
        }

        private void DetectTableParameters(List<List<string>> rows)
        {
            if (rows == null || rows.Count < 2) return;
            List<string> headers = rows[0];
            int colFil = -1, colTd = -1, colCc = -1, colFp = -1;

            string[] filAliases = new string[] { "filial", "codfilial", "cod_filial" };
            string[] tdAliases = new string[] { "tipodoc", "tipo_doc", "codtipodoc", "cod_tipo_doc", "tipo documento", "tipo de documento", "documento tipo" };
            string[] ccAliases = new string[] { "contacaixa", "conta_caixa", "codconta", "cod_conta", "codcontacaixa", "conta caixa", "conta" };
            string[] fpAliases = new string[] { "formapgto", "forma_pgto", "idformapgto", "id_forma_pgto", "forma pagamento", "forma de pagamento" };

            for (int i = 0; i < headers.Count; i++)
            {
                string norm = CsvToOfxConverter.Normalize(headers[i]);
                if (colFil == -1 && CsvToOfxConverter.MatchAny(norm, filAliases)) colFil = i;
                else if (colTd == -1 && CsvToOfxConverter.MatchAny(norm, tdAliases)) colTd = i;
                else if (colCc == -1 && CsvToOfxConverter.MatchAny(norm, ccAliases)) colCc = i;
                else if (colFp == -1 && CsvToOfxConverter.MatchAny(norm, fpAliases)) colFp = i;
            }

            for (int r = 1; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row == null || row.Count == 0) continue;
                if (colFil >= 0 && colFil < row.Count && !string.IsNullOrEmpty(row[colFil].Trim()))
                    currentFilial = row[colFil].Trim();
                if (colTd >= 0 && colTd < row.Count && !string.IsNullOrEmpty(row[colTd].Trim()))
                    currentTipoDoc = row[colTd].Trim();
                if (colCc >= 0 && colCc < row.Count && !string.IsNullOrEmpty(row[colCc].Trim()))
                    currentContaCaixa = row[colCc].Trim();
                if (colFp >= 0 && colFp < row.Count && !string.IsNullOrEmpty(row[colFp].Trim()))
                    currentFormaPgto = CnabToBaixaConverter.NormalizeIdFormaPgto(row[colFp].Trim());
                break;
            }
        }

        private void ResetFileSelection()
        {
            selectedFilePath = null;
            dropZone.Visibility = Visibility.Visible;
            fileInfoPanel.Visibility = Visibility.Collapsed;
            resultPanel.Visibility = Visibility.Collapsed;
        }

        private void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedFilePath) || !File.Exists(selectedFilePath)) return;

            try
            {
                convertButton.IsEnabled = false;
                convertButton.Content = "Convertendo...";

                SaveTotvsConfig();

                string banco = string.IsNullOrEmpty(txtBanco.Text.Trim()) ? "001" : txtBanco.Text.Trim();
                string agencia = string.IsNullOrEmpty(txtAgencia.Text.Trim()) ? "0001" : txtAgencia.Text.Trim();
                string conta = string.IsNullOrEmpty(txtConta.Text.Trim()) ? "123456" : txtConta.Text.Trim();
                string filial = string.IsNullOrEmpty(currentFilial) ? "0002" : currentFilial;
                string tipoDoc = string.IsNullOrEmpty(currentTipoDoc) || currentTipoDoc == "ICOP" || currentTipoDoc == "CRMD" ? "SALP" : currentTipoDoc;
                string contaCaixa = string.IsNullOrEmpty(currentContaCaixa) || currentContaCaixa == "1" ? "30" : currentContaCaixa;
                string formaPgto = CnabToBaixaConverter.NormalizeIdFormaPgto(currentFormaPgto);

                string baseName = Path.GetFileNameWithoutExtension(selectedFilePath);
                string timeTag = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string outExt = targetFormat.ToLower();
                string outputFileName;
                if (targetFormat == "BAIXA")
                {
                    outputFileName = string.Format("BAIXA_{0}_{1}.txt", baseName, timeTag);
                }
                else
                {
                    outputFileName = string.Format("{0}_convertido_{1}.{2}", baseName, timeTag, outExt);
                }
                string outputPath = Path.Combine(documentsDir, outputFileName);

                if (targetFormat == "BAIXA")
                {
                    string baixaContent = "";
                    if (detectedExtension == "xlsx" || detectedExtension == "xls")
                    {
                        var rows = SimpleXlsxReader.ReadRowsFromXlsx(selectedFilePath);
                        baixaContent = CnabToBaixaConverter.ConvertTableToBaixa(rows, filial, tipoDoc, contaCaixa, formaPgto);
                    }
                    else
                    {
                        string rawText = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                        baixaContent = CnabToBaixaConverter.ConvertToBaixa(rawText, filial, tipoDoc, contaCaixa, formaPgto);
                    }
                    File.WriteAllText(outputPath, baixaContent, new UTF8Encoding(true));
                }
                else if ((detectedExtension == "csv" || detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem") && targetFormat == "OFX")
                {
                    string csvContent = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string ofx = CsvToOfxConverter.ConvertToTotvsOfx(csvContent, banco, agencia, conta);
                    File.WriteAllText(outputPath, ofx, Encoding.GetEncoding(1252));
                }
                else if ((detectedExtension == "csv" || detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem") && targetFormat == "XLSX")
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> lines = new List<string>();
                    foreach (string l in rawLines) { string t = l.TrimEnd(); if (!string.IsNullOrEmpty(t)) lines.Add(t); }

                    List<List<string>> rows;
                    if (CnabToBaixaConverter.IsBaixaRmLayout(lines))
                    {
                        rows = CnabToBaixaConverter.ParseBaixaRmToRows(lines);
                    }
                    else
                    {
                        char delim = CsvToOfxConverter.DetectDelimiter(rawText);
                        rows = CsvToOfxConverter.ParseCsv(rawText, delim);
                    }
                    SimpleXlsxWriter.WriteRowsToXlsx(rows, outputPath);
                }
                else if ((detectedExtension == "csv" || detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem") && targetFormat == "CSV")
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> lines = new List<string>();
                    foreach (string l in rawLines) { string t = l.TrimEnd(); if (!string.IsNullOrEmpty(t)) lines.Add(t); }

                    if (CnabToBaixaConverter.IsBaixaRmLayout(lines))
                    {
                        var rows = CnabToBaixaConverter.ParseBaixaRmToRows(lines);
                        string csvOut = SimpleXlsxReader.RowsToCsv(rows, ";");
                        File.WriteAllText(outputPath, csvOut, new UTF8Encoding(true));
                    }
                    else
                    {
                        File.WriteAllText(outputPath, rawText, new UTF8Encoding(true));
                    }
                }
                else if ((detectedExtension == "csv" || detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem") && targetFormat == "TXT")
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> lines = new List<string>();
                    foreach (string l in rawLines) { string t = l.TrimEnd(); if (!string.IsNullOrEmpty(t)) lines.Add(t); }

                    string txt;
                    if (CnabToBaixaConverter.IsBaixaRmLayout(lines))
                    {
                        var rows = CnabToBaixaConverter.ParseBaixaRmToRows(lines);
                        string csvTemp = SimpleXlsxReader.RowsToCsv(rows, ";");
                        txt = TableFormatter.CsvToAlignedTxt(csvTemp);
                    }
                    else
                    {
                        txt = TableFormatter.CsvToAlignedTxt(rawText);
                    }
                    File.WriteAllText(outputPath, txt, Encoding.UTF8);
                }
                else if ((detectedExtension == "csv" || detectedExtension == "txt" || detectedExtension == "ret" || detectedExtension == "rem") && targetFormat == "PDF")
                {
                    string rawText = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> lines = new List<string>();
                    foreach (string l in rawLines) { string t = l.TrimEnd(); if (!string.IsNullOrEmpty(t)) lines.Add(t); }

                    if (CnabToBaixaConverter.IsBaixaRmLayout(lines))
                    {
                        var rows = CnabToBaixaConverter.ParseBaixaRmToRows(lines);
                        string csvTemp = SimpleXlsxReader.RowsToCsv(rows, ";");
                        SimplePdfWriter.WriteCsvToPdf(csvTemp, outputPath, baseName);
                    }
                    else
                    {
                        SimplePdfWriter.WriteCsvToPdf(rawText, outputPath, baseName);
                    }
                }
                else if (detectedExtension == "ofx" && targetFormat == "CSV")
                {
                    string ofxContent = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string csv = OfxParser.ToCsv(ofxContent);
                    File.WriteAllText(outputPath, csv, new UTF8Encoding(true));
                }
                else if (detectedExtension == "ofx" && targetFormat == "XLSX")
                {
                    string ofxContent = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string csv = OfxParser.ToCsv(ofxContent);
                    var rows = CsvToOfxConverter.ParseCsv(csv, ';');
                    SimpleXlsxWriter.WriteRowsToXlsx(rows, outputPath);
                }
                else if (detectedExtension == "ofx" && targetFormat == "TXT")
                {
                    string ofxContent = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string txt = OfxParser.ToTxt(ofxContent);
                    File.WriteAllText(outputPath, txt, Encoding.UTF8);
                }
                else if (detectedExtension == "ofx" && targetFormat == "PDF")
                {
                    string ofxContent = CsvToOfxConverter.ReadAllTextAuto(selectedFilePath);
                    string csv = OfxParser.ToCsv(ofxContent);
                    SimplePdfWriter.WriteCsvToPdf(csv, outputPath, baseName);
                }
                else if ((detectedExtension == "xlsx" || detectedExtension == "xls") && targetFormat == "OFX")
                {
                    var rows = SimpleXlsxReader.ReadRowsFromXlsx(selectedFilePath);
                    string csv = SimpleXlsxReader.RowsToCsv(rows, ";");
                    string ofx = CsvToOfxConverter.ConvertToTotvsOfx(csv, banco, agencia, conta);
                    File.WriteAllText(outputPath, ofx, Encoding.GetEncoding(1252));
                }
                else if ((detectedExtension == "xlsx" || detectedExtension == "xls") && targetFormat == "CSV")
                {
                    var rows = SimpleXlsxReader.ReadRowsFromXlsx(selectedFilePath);
                    string csv = SimpleXlsxReader.RowsToCsv(rows, ";");
                    File.WriteAllText(outputPath, csv, new UTF8Encoding(true));
                }
                else if ((detectedExtension == "xlsx" || detectedExtension == "xls") && targetFormat == "TXT")
                {
                    var rows = SimpleXlsxReader.ReadRowsFromXlsx(selectedFilePath);
                    string csv = SimpleXlsxReader.RowsToCsv(rows, ";");
                    string txt = TableFormatter.CsvToAlignedTxt(csv);
                    File.WriteAllText(outputPath, txt, Encoding.UTF8);
                }
                else if ((detectedExtension == "xlsx" || detectedExtension == "xls") && targetFormat == "PDF")
                {
                    var rows = SimpleXlsxReader.ReadRowsFromXlsx(selectedFilePath);
                    string csv = SimpleXlsxReader.RowsToCsv(rows, ";");
                    SimplePdfWriter.WriteCsvToPdf(csv, outputPath, baseName);
                }
                else
                {
                    File.Copy(selectedFilePath, outputPath, true);
                }

                lastSavedPath = outputPath;
                resultPathText.Text = outputPath;
                resultPanel.Visibility = Visibility.Visible;
                fileInfoPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro na conversão: " + ex.Message, "Falha", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                convertButton.IsEnabled = true;
                convertButton.Content = "CONVERTER ARQUIVO";
            }
        }
    }

    // =========================================================================
    // CONVERSOR CSV -> OFX 100% COMPATÍVEL COM PADRÃO TOTVS (LINHA RM / PROTHEUS)
    // =========================================================================
    public static class CsvToOfxConverter
    {
        public static string ConvertToTotvsOfx(string csvText, string bankId, string branchId, string acctId)
        {
            char delim = DetectDelimiter(csvText);
            List<List<string>> rows = ParseCsv(csvText, delim);

            if (rows.Count < 2) throw new Exception("Arquivo CSV sem dados suficientes para conversão.");

            List<string> headers = rows[0];
            int dateCol = -1, descCol = -1, amountCol = -1, docCol = -1;

            // Identificação inteligente das colunas do extrato
            string[] dateAliases = new string[] { "data", "date", "dt", "lancamento", "transacao", "dia" };
            string[] descAliases = new string[] { "historico", "descricao", "memo", "description", "detalhe", "estabelecimento", "favorecido", "titulo" };
            string[] amountAliases = new string[] { "valor", "amount", "quantia", "total", "debito", "credito", "saida", "entrada" };
            string[] docAliases = new string[] { "documento", "doc", "identificador", "numdoc", "numero", "ndoc", "cheque" };

            for (int i = 0; i < headers.Count; i++)
            {
                string norm = Normalize(headers[i]);
                if (dateCol == -1 && MatchAny(norm, dateAliases)) dateCol = i;
                else if (descCol == -1 && MatchAny(norm, descAliases)) descCol = i;
                else if (amountCol == -1 && MatchAny(norm, amountAliases)) amountCol = i;
                else if (docCol == -1 && MatchAny(norm, docAliases)) docCol = i;
            }

            // Fallback por posição se necessário
            if (dateCol == -1 && headers.Count >= 1) dateCol = 0;
            if (descCol == -1 && headers.Count >= 2) descCol = 1;
            if (amountCol == -1 && headers.Count >= 3) amountCol = 2;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("OFXHEADER:100");
            sb.AppendLine("DATA:OFXSGML");
            sb.AppendLine("VERSION:102");
            sb.AppendLine("SECURITY:NONE");
            sb.AppendLine("ENCODING:USASCII");
            sb.AppendLine("CHARSET:1252");
            sb.AppendLine("COMPRESSION:NONE");
            sb.AppendLine("OLDFILEUID:NONE");
            sb.AppendLine("NEWFILEUID:NONE\r\n");

            string nowStr = DateTime.Now.ToString("yyyyMMddHHmmss") + "[-3:BRT]";

            sb.AppendLine("<OFX>");
            sb.AppendLine("<SIGNONMSGSRSV1>");
            sb.AppendLine("<SONRS>");
            sb.AppendLine("<STATUS>");
            sb.AppendLine("<CODE>0</CODE>");
            sb.AppendLine("<SEVERITY>INFO</SEVERITY>");
            sb.AppendLine("</STATUS>");
            sb.AppendLine(string.Format("<DTSERVER>{0}</DTSERVER>", nowStr));
            sb.AppendLine("<LANGUAGE>POR</LANGUAGE>");
            sb.AppendLine("</SONRS>");
            sb.AppendLine("</SIGNONMSGSRSV1>");

            sb.AppendLine("<BANKMSGSRSV1>");
            sb.AppendLine("<STMTTRNRS>");
            sb.AppendLine("<TRNUID>1001</TRNUID>");
            sb.AppendLine("<STATUS>");
            sb.AppendLine("<CODE>0</CODE>");
            sb.AppendLine("<SEVERITY>INFO</SEVERITY>");
            sb.AppendLine("</STATUS>");
            sb.AppendLine("<STMTRS>");
            sb.AppendLine("<CURDEF>BRL</CURDEF>");

            // Informações Bancárias para Conciliação TOTVS (FCXA.NUMBANCO, FCXA.CODAGENCIA, FCXA.CODCONTA)
            sb.AppendLine("<BANKACCTFROM>");
            sb.AppendLine(string.Format("<BANKID>{0}</BANKID>", bankId));
            if (!string.IsNullOrEmpty(branchId))
            {
                sb.AppendLine(string.Format("<BRANCHID>{0}</BRANCHID>", branchId));
            }
            sb.AppendLine(string.Format("<ACCTID>{0}</ACCTID>", acctId));
            sb.AppendLine("<ACCTTYPE>CHECKING</ACCTTYPE>");
            sb.AppendLine("</BANKACCTFROM>");

            sb.AppendLine("<BANKTRANLIST>");

            string minDate = "99999999";
            string maxDate = "00000000";
            decimal totalBalance = 0;
            int count = 0;

            StringBuilder trnList = new StringBuilder();

            for (int r = 1; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row.Count == 0) continue;

                string rawDate = dateCol < row.Count ? row[dateCol] : "";
                string rawDesc = descCol < row.Count ? row[descCol] : "";
                string rawAmount = amountCol < row.Count ? row[amountCol] : "";
                string rawDoc = (docCol != -1 && docCol < row.Count) ? row[docCol] : "";

                if (string.IsNullOrEmpty(rawDate) && string.IsNullOrEmpty(rawAmount)) continue;

                string parsedDate = ParseDate(rawDate);
                decimal parsedAmount = ParseAmount(rawAmount);

                if (string.IsNullOrEmpty(parsedDate)) continue;

                if (string.Compare(parsedDate, minDate) < 0) minDate = parsedDate;
                if (string.Compare(parsedDate, maxDate) > 0) maxDate = parsedDate;
                totalBalance += parsedAmount;
                count++;

                string trnType = parsedAmount < 0 ? "DEBIT" : "CREDIT";
                string cleanMemo = CleanXml(string.IsNullOrEmpty(rawDesc) ? "Transacao" : rawDesc);
                
                // Mapeamento TOTVS: FITID vai para FXCX.NUMEROODOCUMENTO
                string fitId = !string.IsNullOrEmpty(rawDoc) ? CleanXml(rawDoc) : string.Format("{0}{1:D4}", parsedDate, count);
                string checkNum = !string.IsNullOrEmpty(rawDoc) ? CleanXml(rawDoc) : count.ToString();

                // Bloco de Transação formatado com tags fechadas (Padrão TOTVS)
                trnList.AppendLine("<STMTTRN>");
                trnList.AppendLine(string.Format("<TRNTYPE>{0}</TRNTYPE>", trnType));
                trnList.AppendLine(string.Format("<DTPOSTED>{0}120000[-3:BRT]</DTPOSTED>", parsedDate));
                trnList.AppendLine(string.Format("<TRNAMT>{0}</TRNAMT>", parsedAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
                trnList.AppendLine(string.Format("<FITID>{0}</FITID>", fitId));
                trnList.AppendLine(string.Format("<CHECKNUM>{0}</CHECKNUM>", checkNum));
                trnList.AppendLine(string.Format("<MEMO>{0}</MEMO>", cleanMemo));
                trnList.AppendLine("</STMTTRN>");
            }

            if (minDate == "99999999") minDate = DateTime.Now.ToString("yyyyMMdd");
            if (maxDate == "00000000") maxDate = DateTime.Now.ToString("yyyyMMdd");

            sb.AppendLine(string.Format("<DTSTART>{0}120000[-3:BRT]</DTSTART>", minDate));
            sb.AppendLine(string.Format("<DTEND>{0}120000[-3:BRT]</DTEND>", maxDate));
            sb.Append(trnList.ToString());
            sb.AppendLine("</BANKTRANLIST>");

            // Identificação do Saldo Bancário para o TOTVS (FCXA.SALDOBANCARIO e FCXA.DATASALDOBANCARIO)
            sb.AppendLine("<LEDGERBAL>");
            sb.AppendLine(string.Format("<BALAMT>{0}</BALAMT>", totalBalance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine(string.Format("<DTASOF>{0}120000[-3:BRT]</DTASOF>", maxDate));
            sb.AppendLine("</LEDGERBAL>");

            sb.AppendLine("</STMTRS>");
            sb.AppendLine("</STMTTRNRS>");
            sb.AppendLine("</BANKMSGSRSV1>");
            sb.AppendLine("</OFX>");

            return sb.ToString();
        }

        public static string ReadAllTextAuto(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            try
            {
                UTF8Encoding utf8Strict = new UTF8Encoding(false, true);
                return utf8Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        public static char DetectDelimiter(string text)
        {
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return ';';
            string sample = lines[0];
            int semicolons = sample.Split(';').Length;
            int commas = sample.Split(',').Length;
            int tabs = sample.Split('\t').Length;

            if (semicolons >= commas && semicolons >= tabs) return ';';
            if (commas >= semicolons && commas >= tabs) return ',';
            return '\t';
        }

        public static List<List<string>> ParseCsv(string text, char delimiter)
        {
            List<List<string>> result = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = (i + 1 < text.Length) ? text[i + 1] : '\0';

                if (c == '"')
                {
                    if (inQuotes && next == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == delimiter && !inQuotes)
                {
                    row.Add(cell.ToString().Trim());
                    cell.Clear();
                }
                else if ((c == '\r' || c == '\n') && !inQuotes)
                {
                    if (c == '\r' && next == '\n') i++;
                    row.Add(cell.ToString().Trim());
                    cell.Clear();
                    if (row.Count > 0 && !(row.Count == 1 && string.IsNullOrEmpty(row[0])))
                    {
                        result.Add(row);
                    }
                    row = new List<string>();
                }
                else
                {
                    cell.Append(c);
                }
            }

            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString().Trim());
                result.Add(row);
            }

            return result;
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.ToLower()
                .Replace("á", "a").Replace("ã", "a").Replace("â", "a")
                .Replace("é", "e").Replace("ê", "e")
                .Replace("í", "i")
                .Replace("ó", "o").Replace("õ", "o").Replace("ô", "o")
                .Replace("ú", "u")
                .Replace("ç", "c")
                .Trim();
        }

        public static bool MatchAny(string text, string[] aliases)
        {
            foreach (string a in aliases)
            {
                if (text.Contains(a)) return true;
            }
            return false;
        }

        public static string ParseDate(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr)) return null;
            dateStr = dateStr.Replace("\"", "").Trim();

            var parts = dateStr.Split(new char[] { '/', '-', '.' });
            if (parts.Length == 3)
            {
                if (parts[0].Length <= 2 && parts[1].Length <= 2 && parts[2].Length == 4)
                {
                    return string.Format("{0}{1}{2}", parts[2], parts[1].PadLeft(2, '0'), parts[0].PadLeft(2, '0'));
                }
                else if (parts[0].Length == 4 && parts[1].Length <= 2 && parts[2].Length <= 2)
                {
                    return string.Format("{0}{1}{2}", parts[0], parts[1].PadLeft(2, '0'), parts[2].PadLeft(2, '0'));
                }
            }

            long dummyDate;
            if (dateStr.Length == 8 && long.TryParse(dateStr, out dummyDate)) return dateStr;
            return DateTime.Now.ToString("yyyyMMdd");
        }

        public static decimal ParseAmount(string valStr)
        {
            if (string.IsNullOrEmpty(valStr)) return 0;
            string s = valStr.Replace("R$", "").Replace(" ", "").Trim();

            bool isNegative = false;
            if (s.StartsWith("-") || s.EndsWith("-") || s.ToUpper().Contains("D") || (s.StartsWith("(") && s.EndsWith(")")))
            {
                isNegative = true;
            }

            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^\d,\.]", "");

            if (s.Contains(",") && s.Contains("."))
            {
                if (s.LastIndexOf(',') > s.LastIndexOf('.'))
                    s = s.Replace(".", "").Replace(',', '.');
                else
                    s = s.Replace(",", "");
            }
            else if (s.Contains(","))
            {
                s = s.Replace(',', '.');
            }

            decimal val;
            if (!decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val))
            {
                return 0;
            }

            return isNegative ? -Math.Abs(val) : val;
        }

        private static string CleanXml(string s)
        {
            return s.Replace("&", "e").Replace("<", "").Replace(">", "").Replace("\"", "").Trim();
        }
    }

    // ==========================================
    // FORMATADOR TXT TABULAR ALINHADO
    // ==========================================
    public static class TableFormatter
    {
        public static string CsvToAlignedTxt(string csv)
        {
            char delim = csv.Contains(";") ? ';' : ',';
            var rows = CsvToOfxConverter.ParseCsv(csv, delim);
            if (rows.Count == 0) return "";

            int cols = rows[0].Count;
            int[] widths = new int[cols];

            foreach (var r in rows)
            {
                for (int i = 0; i < cols && i < r.Count; i++)
                {
                    int len = r[i].Length;
                    if (len > widths[i]) widths[i] = Math.Min(len, 35);
                }
            }

            StringBuilder sb = new StringBuilder();

            sb.AppendLine(FormatRow(rows[0], widths));

            List<string> div = new List<string>();
            for (int i = 0; i < cols; i++) div.Add(new string('-', Math.Max(widths[i], 6)));
            sb.AppendLine(string.Join("-+-", div.ToArray()));

            for (int i = 1; i < rows.Count; i++)
            {
                sb.AppendLine(FormatRow(rows[i], widths));
            }

            return sb.ToString();
        }

        private static string FormatRow(List<string> row, int[] widths)
        {
            List<string> cells = new List<string>();
            for (int i = 0; i < widths.Length; i++)
            {
                string val = (i < row.Count) ? row[i] : "";
                if (val.Length > 35) val = val.Substring(0, 32) + "...";
                cells.Add(val.PadRight(Math.Max(widths[i], 6)));
            }
            return string.Join(" | ", cells.ToArray());
        }
    }

    // ==========================================
    // PARSER OFX PARA CSV E TXT
    // ==========================================
    public static class OfxParser
    {
        public static string ToCsv(string ofx)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Data;Tipo;Descricao;Valor");

            var matches = System.Text.RegularExpressions.Regex.Matches(ofx, @"<STMTTRN>([\s\S]*?)<\/STMTTRN>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string block = m.Groups[1].Value;
                string dt = GetTag(block, "DTPOSTED");
                string type = GetTag(block, "TRNTYPE");
                string amt = GetTag(block, "TRNAMT");
                string memo = GetTag(block, "MEMO").Replace(";", ",");

                if (dt.Length >= 8)
                {
                    dt = string.Format("{0}/{1}/{2}", dt.Substring(6, 2), dt.Substring(4, 2), dt.Substring(0, 4));
                }

                sb.AppendLine(string.Format("{0};{1};\"{2}\";{3}", dt, type, memo, amt));
            }

            return sb.ToString();
        }

        public static string ToTxt(string ofx)
        {
            string csv = ToCsv(ofx);
            return TableFormatter.CsvToAlignedTxt(csv);
        }

        private static string GetTag(string block, string tag)
        {
            var m = System.Text.RegularExpressions.Regex.Match(block, "<" + tag + ">([^<\\r\\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }

    // ==========================================
    // GERADOR DE PDF PURO VETORIAL PADRÃO 1.4
    // ==========================================
    public static class SimplePdfWriter
    {
        public static void WriteCsvToPdf(string csv, string outPdfPath, string title)
        {
            char delim = csv.Contains(";") ? ';' : ',';
            var rows = CsvToOfxConverter.ParseCsv(csv, delim);
            if (rows.Count == 0) return;

            StringBuilder streamContent = new StringBuilder();
            int pageHeight = 842;
            int pageWidth = 595;
            int margin = 35;
            int usableWidth = pageWidth - (margin * 2);

            int cols = rows[0].Count;
            int colW = Math.Max(50, usableWidth / Math.Max(1, cols));

            int y = pageHeight - 50;

            // Título
            streamContent.AppendLine("BT");
            streamContent.AppendLine("/F2 16 Tf");
            streamContent.AppendLine(string.Format("{0} {1} Td", margin, y));
            streamContent.AppendLine(string.Format("({0}) Tj", EscapePdf(title)));
            streamContent.AppendLine("ET");

            y -= 25;
            streamContent.AppendLine("BT");
            streamContent.AppendLine("/F1 8 Tf");
            streamContent.AppendLine(string.Format("{0} {1} Td", margin, y));
            streamContent.AppendLine(string.Format("(Gerado em: {0} - Total de registros: {1}) Tj", DateTime.Now.ToString("dd/MM/yyyy HH:mm"), rows.Count - 1));
            streamContent.AppendLine("ET");

            y -= 25;

            // Header Bar
            streamContent.AppendLine(string.Format("0.1 0.1 0.1 rg"));
            streamContent.AppendLine(string.Format("{0} {1} {2} 18 re f", margin, y, usableWidth));

            // Header Text
            streamContent.AppendLine("BT");
            streamContent.AppendLine("/F2 8 Tf");
            streamContent.AppendLine("1 1 1 rg");
            for (int i = 0; i < cols; i++)
            {
                string text = rows[0][i];
                if (text.Length > 20) text = text.Substring(0, 18) + "..";
                streamContent.AppendLine(string.Format("{0} {1} Td", (i == 0 ? margin + 5 : colW), (i == 0 ? y + 5 : 0)));
                streamContent.AppendLine(string.Format("({0}) Tj", EscapePdf(text)));
            }
            streamContent.AppendLine("ET");

            y -= 18;

            // Data Rows
            int maxRows = Math.Min(rows.Count, 45);
            for (int r = 1; r < maxRows; r++)
            {
                bool isEven = (r % 2 == 0);
                if (isEven)
                {
                    streamContent.AppendLine("0.95 0.95 0.95 rg");
                    streamContent.AppendLine(string.Format("{0} {1} {2} 15 re f", margin, y, usableWidth));
                }

                streamContent.AppendLine("BT");
                streamContent.AppendLine("/F1 8 Tf");
                streamContent.AppendLine("0 0 0 rg");

                for (int i = 0; i < cols && i < rows[r].Count; i++)
                {
                    string text = rows[r][i];
                    if (text.Length > 25) text = text.Substring(0, 22) + "..";
                    streamContent.AppendLine(string.Format("{0} {1} Td", (i == 0 ? margin + 5 : colW), (i == 0 ? y + 4 : 0)));
                    streamContent.AppendLine(string.Format("({0}) Tj", EscapePdf(text)));
                }

                streamContent.AppendLine("ET");
                y -= 15;
            }

            string streamStr = streamContent.ToString();
            byte[] streamBytes = Encoding.ASCII.GetBytes(streamStr);

            using (FileStream fs = new FileStream(outPdfPath, FileMode.Create, FileAccess.Write))
            using (StreamWriter sw = new StreamWriter(fs, Encoding.ASCII))
            {
                sw.Write("%PDF-1.4\r\n");

                long obj1 = fs.Position;
                sw.Write("1 0 obj\r\n<< /Type /Catalog /Pages 2 0 R >>\r\nendobj\r\n");

                long obj2 = fs.Position;
                sw.Write("2 0 obj\r\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\r\nendobj\r\n");

                long obj3 = fs.Position;
                sw.Write(string.Format("3 0 obj\r\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>\r\nendobj\r\n"));

                long obj4 = fs.Position;
                sw.Write("4 0 obj\r\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\r\nendobj\r\n");

                long obj5 = fs.Position;
                sw.Write("5 0 obj\r\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>\r\nendobj\r\n");

                long obj6 = fs.Position;
                sw.Write(string.Format("6 0 obj\r\n<< /Length {0} >>\r\nstream\r\n", streamBytes.Length));
                sw.Flush();
                fs.Write(streamBytes, 0, streamBytes.Length);
                sw.Write("\r\nendstream\r\nendobj\r\n");

                long xrefPos = fs.Position;
                sw.Write("xref\r\n0 7\r\n0000000000 65535 f \r\n");
                sw.Write(string.Format("{0:D10} 00000 n \r\n", obj1));
                sw.Write(string.Format("{0:D10} 00000 n \r\n", obj2));
                sw.Write(string.Format("{0:D10} 00000 n \r\n", obj3));
                sw.Write(string.Format("{0:D10} 00000 n \r\n", obj4));
                sw.Write(string.Format("{0:D10} 00000 n \r\n", obj5));
                sw.Write(string.Format("{0:D10} 00000 n \r\n", obj6));

                sw.Write("trailer\r\n<< /Size 7 /Root 1 0 R >>\r\nstartxref\r\n");
                sw.Write(string.Format("{0}\r\n%%EOF\r\n", xrefPos));
            }
        }

        private static string EscapePdf(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }
    }

    // ==========================================
    // GERADOR NATIVO DE ARQUIVOS EXCEL (.XLSX)
    // ==========================================
    public static class SimpleXlsxWriter
    {
        public static void WriteRowsToXlsx(List<List<string>> rows, string outXlsxPath)
        {
            if (File.Exists(outXlsxPath)) File.Delete(outXlsxPath);

            using (Package package = Package.Open(outXlsxPath, FileMode.Create))
            {
                // 1. Workbook part
                Uri wbUri = new Uri("/xl/workbook.xml", UriKind.Relative);
                PackagePart wbPart = package.CreatePart(wbUri, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                string wbXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\r\n" +
                    "  <sheets>\r\n" +
                    "    <sheet name=\"Planilha1\" sheetId=\"1\" r:id=\"rId1\"/>\r\n" +
                    "  </sheets>\r\n" +
                    "</workbook>";
                byte[] wbBytes = Encoding.UTF8.GetBytes(wbXml);
                wbPart.GetStream().Write(wbBytes, 0, wbBytes.Length);
                package.CreateRelationship(wbUri, TargetMode.Internal, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "rId1");

                // 2. Styles part (Header estilizado com negrito, fundo suave e borda)
                Uri stylesUri = new Uri("/xl/styles.xml", UriKind.Relative);
                PackagePart stylesPart = package.CreatePart(stylesUri, "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                string stylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n" +
                    "  <fonts count=\"2\">\r\n" +
                    "    <font><sz val=\"11\"/><name val=\"Calibri\"/></font>\r\n" +
                    "    <font><b/><sz val=\"11\"/><color rgb=\"FF1F2937\"/><name val=\"Calibri\"/></font>\r\n" +
                    "  </fonts>\r\n" +
                    "  <fills count=\"3\">\r\n" +
                    "    <fill><patternFill patternType=\"none\"/></fill>\r\n" +
                    "    <fill><patternFill patternType=\"gray125\"/></fill>\r\n" +
                    "    <fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF3F4F6\"/></patternFill></fill>\r\n" +
                    "  </fills>\r\n" +
                    "  <borders count=\"2\">\r\n" +
                    "    <border><left/><right/><top/><bottom/><diagonal/></border>\r\n" +
                    "    <border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FFD1D5DB\"/></bottom><diagonal/></border>\r\n" +
                    "  </borders>\r\n" +
                    "  <cellStyleXfs count=\"1\">\r\n" +
                    "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>\r\n" +
                    "  </cellStyleXfs>\r\n" +
                    "  <cellXfs count=\"2\">\r\n" +
                    "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>\r\n" +
                    "    <xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>\r\n" +
                    "  </cellXfs>\r\n" +
                    "</styleSheet>";
                byte[] stylesBytes = Encoding.UTF8.GetBytes(stylesXml);
                stylesPart.GetStream().Write(stylesBytes, 0, stylesBytes.Length);

                // 3. Worksheet part
                Uri sheetUri = new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative);
                PackagePart sheetPart = package.CreatePart(sheetUri, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");

                // Cálculo automático da largura das colunas
                int maxCols = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    if (rows[r].Count > maxCols) maxCols = rows[r].Count;
                }

                double[] colWidths = new double[maxCols];
                for (int c = 0; c < maxCols; c++) colWidths[c] = 10.0;

                for (int r = 0; r < rows.Count; r++)
                {
                    for (int c = 0; c < rows[r].Count; c++)
                    {
                        string val = rows[r][c];
                        if (!string.IsNullOrEmpty(val))
                        {
                            if (val.Length > colWidths[c]) colWidths[c] = val.Length;
                        }
                    }
                }

                StringBuilder sheetXml = new StringBuilder();
                sheetXml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
                sheetXml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\r\n");

                if (maxCols > 0)
                {
                    sheetXml.Append("  <cols>\r\n");
                    for (int c = 0; c < maxCols; c++)
                    {
                        double w = Math.Max(12.0, Math.Min(colWidths[c] + 4.0, 60.0));
                        sheetXml.Append(string.Format(CultureInfo.InvariantCulture, "    <col min=\"{0}\" max=\"{0}\" width=\"{1:0.0}\" customWidth=\"1\"/>\r\n", c + 1, w));
                    }
                    sheetXml.Append("  </cols>\r\n");
                }

                sheetXml.Append("  <sheetData>\r\n");

                for (int r = 0; r < rows.Count; r++)
                {
                    List<string> row = rows[r];
                    int rowIdx = r + 1;
                    sheetXml.Append(string.Format("    <row r=\"{0}\">\r\n", rowIdx));

                    for (int c = 0; c < row.Count; c++)
                    {
                        string cellRef = GetColumnLetter(c + 1) + rowIdx;
                        string val = row[c] != null ? row[c] : "";

                        if (r == 0)
                        {
                            // Cabeçalho sempre texto estilizado (s=\"1\")
                            sheetXml.Append(string.Format("      <c r=\"{0}\" t=\"inlineStr\" s=\"1\"><is><t>{1}</t></is></c>\r\n",
                                cellRef, EscapeXml(val)));
                        }
                        else
                        {
                            double num;
                            if (TryFormatNumber(val, out num))
                            {
                                sheetXml.Append(string.Format("      <c r=\"{0}\"><v>{1}</v></c>\r\n",
                                    cellRef, num.ToString(CultureInfo.InvariantCulture)));
                            }
                            else
                            {
                                sheetXml.Append(string.Format("      <c r=\"{0}\" t=\"inlineStr\"><is><t>{1}</t></is></c>\r\n",
                                    cellRef, EscapeXml(val)));
                            }
                        }
                    }

                    sheetXml.Append("    </row>\r\n");
                }

                sheetXml.Append("  </sheetData>\r\n");
                sheetXml.Append("</worksheet>");

                byte[] sheetBytes = Encoding.UTF8.GetBytes(sheetXml.ToString());
                sheetPart.GetStream().Write(sheetBytes, 0, sheetBytes.Length);

                wbPart.CreateRelationship(sheetUri, TargetMode.Internal, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "rId1");
                wbPart.CreateRelationship(stylesUri, TargetMode.Internal, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "rId2");
            }
        }

        private static bool TryFormatNumber(string s, out double num)
        {
            num = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string trimmed = s.Trim();

            if (trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.' && trimmed[1] != ',') return false;
            if (trimmed.Length > 2 && trimmed[0] == '-' && trimmed[1] == '0' && trimmed[2] != '.' && trimmed[2] != ',') return false;
            if (trimmed.Contains("/") || trimmed.Contains(":") || (trimmed.Contains("-") && !trimmed.StartsWith("-"))) return false;

            string clean = trimmed.Replace("R$", "").Replace(" ", "").Trim();

            if (clean.Contains(",") && clean.Contains("."))
            {
                if (clean.LastIndexOf(',') > clean.LastIndexOf('.'))
                    clean = clean.Replace(".", "").Replace(',', '.');
                else
                    clean = clean.Replace(",", "");
            }
            else if (clean.Contains(","))
            {
                clean = clean.Replace(',', '.');
            }

            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out num);
        }

        public static string GetColumnLetter(int colIndex)
        {
            int div = colIndex;
            string colLetter = String.Empty;
            while (div > 0)
            {
                int mod = (div - 1) % 26;
                colLetter = (char)(65 + mod) + colLetter;
                div = (int)((div - mod) / 26);
            }
            return colLetter;
        }

        public static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }
    }

    // ==========================================
    // LEITOR NATIVO DE ARQUIVOS EXCEL (.XLSX)
    // ==========================================
    public static class SimpleXlsxReader
    {
        public static List<List<string>> ReadRowsFromXlsx(string path)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> sharedStrings = new List<string>();

            using (Package package = Package.Open(path, FileMode.Open, FileAccess.Read))
            {
                Uri ssUri = new Uri("/xl/sharedStrings.xml", UriKind.Relative);
                if (package.PartExists(ssUri))
                {
                    PackagePart ssPart = package.GetPart(ssUri);
                    XmlDocument ssDoc = new XmlDocument();
                    ssDoc.Load(ssPart.GetStream());
                    XmlNodeList siNodes = ssDoc.GetElementsByTagName("si");
                    foreach (XmlElement si in siNodes)
                    {
                        XmlNodeList tNodes = si.GetElementsByTagName("t");
                        StringBuilder sb = new StringBuilder();
                        foreach (XmlNode t in tNodes) sb.Append(t.InnerText);
                        sharedStrings.Add(sb.ToString());
                    }
                }

                Uri sheetUri = new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative);
                if (!package.PartExists(sheetUri))
                {
                    foreach (PackagePart part in package.GetParts())
                    {
                        if (part.Uri.ToString().ToLower().Contains("/worksheets/sheet"))
                        {
                            sheetUri = part.Uri;
                            break;
                        }
                    }
                }

                if (package.PartExists(sheetUri))
                {
                    PackagePart sheetPart = package.GetPart(sheetUri);
                    XmlDocument sheetDoc = new XmlDocument();
                    sheetDoc.Load(sheetPart.GetStream());
                    XmlNodeList rowNodes = sheetDoc.GetElementsByTagName("row");
                    foreach (XmlElement rowNode in rowNodes)
                    {
                        List<string> row = new List<string>();
                        XmlNodeList cellNodes = rowNode.GetElementsByTagName("c");
                        int currentCol = 0;
                        foreach (XmlElement c in cellNodes)
                        {
                            string rAttr = c.GetAttribute("r");
                            int colIdx = GetColumnIndex(rAttr);
                            while (currentCol < colIdx)
                            {
                                row.Add("");
                                currentCol++;
                            }

                            string tAttr = c.GetAttribute("t");
                            string val = "";
                            if (tAttr == "s")
                            {
                                XmlNodeList vList = c.GetElementsByTagName("v");
                                if (vList.Count > 0)
                                {
                                    int idx;
                                    if (int.TryParse(vList[0].InnerText, out idx) && idx >= 0 && idx < sharedStrings.Count)
                                    {
                                        val = sharedStrings[idx];
                                    }
                                }
                            }
                            else if (tAttr == "inlineStr")
                            {
                                XmlNodeList tList = c.GetElementsByTagName("t");
                                if (tList.Count > 0) val = tList[0].InnerText;
                            }
                            else
                            {
                                XmlNodeList vList = c.GetElementsByTagName("v");
                                if (vList.Count > 0) val = vList[0].InnerText;
                            }

                            row.Add(val);
                            currentCol++;
                        }
                        if (row.Count > 0)
                        {
                            rows.Add(row);
                        }
                    }
                }
            }

            return rows;
        }

        private static int GetColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return 0;
            int col = 0;
            for (int i = 0; i < cellRef.Length; i++)
            {
                char ch = cellRef[i];
                if (ch >= 'A' && ch <= 'Z')
                {
                    col = col * 26 + (ch - 'A' + 1);
                }
                else break;
            }
            return Math.Max(0, col - 1);
        }

        public static string RowsToCsv(List<List<string>> rows, string delimiter)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var row in rows)
            {
                List<string> escapedCells = new List<string>();
                foreach (var cell in row)
                {
                    string c = cell != null ? cell : "";
                    if (c.Contains(delimiter) || c.Contains("\"") || c.Contains("\n") || c.Contains("\r"))
                    {
                        escapedCells.Add("\"" + c.Replace("\"", "\"\"") + "\"");
                    }
                    else
                    {
                        escapedCells.Add(c);
                    }
                }
                sb.AppendLine(string.Join(delimiter, escapedCells.ToArray()));
            }
            return sb.ToString();
        }
    }

    // =========================================================================
    // CONVERSOR CNAB / CSV -> LAYOUT BAIXA TOTVS RM (937 CARACTERES, LINHA L)
    // =========================================================================
    public static class CnabToBaixaConverter
    {
        public static bool IsBaixaRmLayout(List<string> lines)
        {
            if (lines == null || lines.Count == 0) return false;
            int rmMatches = 0;
            int checkCount = Math.Min(lines.Count, 15);
            for (int i = 0; i < checkCount; i++)
            {
                string l = lines[i];
                if (l.Length >= 900 && l.StartsWith("L"))
                {
                    rmMatches++;
                }
            }
            return (rmMatches > 0 && (rmMatches * 2 >= checkCount || rmMatches >= 3));
        }

        public static decimal ParseBaixaDecimal(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0m;
            str = str.Trim();
            decimal res;
            if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out res))
            {
                return res;
            }
            if (decimal.TryParse(str, NumberStyles.Any, new CultureInfo("pt-BR"), out res))
            {
                return res;
            }
            return 0m;
        }

        public static string NormalizeIdFormaPgto(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "3";
            raw = raw.Trim();
            if (raw.Length == 0) return "3";

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(\d+)");
            if (m.Success)
            {
                string num = m.Groups[1].Value;
                if (num == "12") return "3"; // Usuário solicitou: o 26 que era pix e virou 12, muda para ser o retorno bancario (3)
                return num;
            }

            string upper = raw.ToUpperInvariant();

            // Retorno Bancário / Boleto / Cobrança / Banco / PIX -> 3
            if (upper.Contains("RETORNO") ||
                upper.Contains("BANC") ||
                upper.Contains("BOL") ||
                upper.Contains("COB") ||
                upper.Contains("TIT") ||
                upper.Contains("PIX") ||
                upper == "BA")
            {
                return "3";
            }

            // Depósito / TED / DOC -> 12
            if (upper.Contains("DEP") ||
                upper.Contains("TRANSF") ||
                upper.Contains("TED") ||
                upper.Contains("DOC") ||
                upper == "DE")
            {
                return "12";
            }

            // Dinheiro / Espécie / Caixa -> 1
            if (upper.Contains("DIN") ||
                upper.Contains("ESP") ||
                upper.Contains("CAIX") ||
                upper == "DI")
            {
                return "1";
            }

            // Cheque -> 6
            if (upper.Contains("CHEQ") || upper == "CH")
            {
                return "6";
            }

            // Cartão de Crédito -> 9
            if (upper.Contains("CRED") || upper == "VC" || upper == "RC")
            {
                return "9";
            }

            // Cartão de Débito -> 13
            if (upper.Contains("DEB") || upper == "VD" || upper == "RD")
            {
                return "13";
            }

            return "3";
        }

        public static string GetFormaPgtoNome(string idOrName)
        {
            string id = NormalizeIdFormaPgto(idOrName);
            switch (id)
            {
                case "3": return "Retorno Bancário";
                case "12": return "DEPÓSITO";
                case "1": return "DINHEIRO";
                case "6": return "CHEQUE";
                case "9":
                case "14": return "CARTÃO CRÉDITO";
                case "13":
                case "2": return "CARTÃO DÉBITO";
                default: return "Retorno Bancário";
            }
        }

        public static string ProcessBaixaRmFile(List<string> lines, string fallbackFilial, string fallbackTipoDoc, string fallbackContaCaixa, string fallbackFormaPgto)
        {
            List<string> resultLines = new List<string>();
            foreach (string l in lines)
            {
                if (l.Length >= 900 && l.StartsWith("L"))
                {
                    string fil = l.Substring(1, 4).Trim();
                    string clifor = l.Substring(5, 25).Trim();
                    string td = l.Substring(30, 10).Trim();
                    string numDoc = l.Substring(40, 40).Trim();
                    string dtBx = l.Substring(80, 6).Trim();

                    string vlrBxStr = l.Substring(86, 18).Trim();
                    string vlrJrStr = l.Substring(104, 18).Trim();
                    string vlrDescStr = l.Substring(122, 18).Trim();

                    string cc = l.Substring(144, 10).Trim();
                    string vlrMultaStr = (l.Length >= 348) ? l.Substring(330, 18).Trim() : "0";
                    string hist = (l.Length >= 627) ? l.Substring(372, 255).Trim() : "";
                    string fp = (l.Length >= 937) ? l.Substring(930, 7).Trim() : (l.Length >= 931 ? l.Substring(930).Trim() : "");
                    string existingIdLan = (l.Length >= 852) ? l.Substring(752, 100).Trim() : "";

                    decimal vlrBx = ParseBaixaDecimal(vlrBxStr);
                    decimal vlrJr = ParseBaixaDecimal(vlrJrStr);
                    decimal vlrDesc = ParseBaixaDecimal(vlrDescStr);
                    decimal vlrMulta = ParseBaixaDecimal(vlrMultaStr);

                    // Consulta automática ao banco TOTVS RM na tabela FLAN
                    var flan = TotvsDbService.QueryLancamento(numDoc, clifor, hist, vlrBx);
                    if (flan.Found)
                    {
                        if (!string.IsNullOrEmpty(flan.CodTipoDoc)) td = flan.CodTipoDoc;
                        if (!string.IsNullOrEmpty(flan.IdLan)) existingIdLan = flan.IdLan;
                        if (!string.IsNullOrEmpty(flan.CodFilial)) fil = flan.CodFilial;
                        if (!string.IsNullOrEmpty(flan.CodConta)) cc = flan.CodConta;
                        if (!string.IsNullOrEmpty(flan.CodCfo)) clifor = flan.CodCfo;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(flan.CodCfo)) clifor = flan.CodCfo;
                        if (string.IsNullOrEmpty(td) || td == "IGRA") td = fallbackTipoDoc;
                        if (string.IsNullOrEmpty(fil)) fil = fallbackFilial;
                        if (string.IsNullOrEmpty(cc)) cc = fallbackContaCaixa;
                    }
                    if (string.IsNullOrEmpty(fp)) fp = fallbackFormaPgto;
                    fp = NormalizeIdFormaPgto(fp);
                    if (string.IsNullOrEmpty(fp) || fp == "12") fp = "3";

                    string lineBx = BuildBaixaLineDirect(
                        fil,
                        clifor,
                        td,
                        numDoc,
                        dtBx,
                        vlrBx,
                        vlrJr,
                        vlrDesc,
                        vlrMulta,
                        cc,
                        hist,
                        fp,
                        existingIdLan
                    );
                    resultLines.Add(lineBx);
                }
            }

            if (resultLines.Count == 0) return "";
            return string.Join("\r\n", resultLines.ToArray()) + "\r\n";
        }

        public static List<List<string>> ParseBaixaRmToRows(List<string> lines)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> header = new List<string>()
            {
                "IDLAN",
                "Filial",
                "Cliente_Fornecedor",
                "Tipo_Documento",
                "Numero_Documento",
                "Data_Baixa",
                "Valor_Baixado",
                "Valor_Juros",
                "Valor_Desconto",
                "Conta_Caixa",
                "Historico",
                "Forma_Pagamento"
            };
            rows.Add(header);

            CultureInfo ptBr = new CultureInfo("pt-BR");
            foreach (string l in lines)
            {
                if (l.Length >= 900 && l.StartsWith("L"))
                {
                    string fil = l.Substring(1, 4).Trim();
                    string clifor = l.Substring(5, 25).Trim();
                    string td = l.Substring(30, 10).Trim();
                    string numDoc = l.Substring(40, 40).Trim();
                    string dtBx = l.Substring(80, 6).Trim();
                    string dtFormatted = dtBx;
                    if (dtBx.Length == 6)
                    {
                        dtFormatted = dtBx.Substring(0, 2) + "/" + dtBx.Substring(2, 2) + "/20" + dtBx.Substring(4, 2);
                    }

                    decimal vlrBx = ParseBaixaDecimal(l.Substring(86, 18));
                    decimal vlrJr = ParseBaixaDecimal(l.Substring(104, 18));
                    decimal vlrDesc = ParseBaixaDecimal(l.Substring(122, 18));

                    string cc = l.Substring(144, 10).Trim();
                    string hist = (l.Length >= 627) ? l.Substring(372, 255).Trim() : "";
                    string fp = (l.Length >= 937) ? l.Substring(930, 7).Trim() : (l.Length >= 931 ? l.Substring(930).Trim() : "");
                    fp = NormalizeIdFormaPgto(fp);
                    string idLan = (l.Length >= 852) ? l.Substring(752, 100).Trim() : "";
                    if (string.IsNullOrEmpty(idLan) && hist.Contains("IDLAN:"))
                    {
                        int idx = hist.IndexOf("IDLAN:");
                        idLan = hist.Substring(idx + 6).Trim();
                    }

                    List<string> row = new List<string>()
                    {
                        idLan,
                        fil,
                        clifor,
                        td,
                        numDoc,
                        dtFormatted,
                        vlrBx.ToString("N2", ptBr),
                        vlrJr.ToString("N2", ptBr),
                        vlrDesc.ToString("N2", ptBr),
                        cc,
                        hist,
                        fp
                    };
                    rows.Add(row);
                }
            }
            return rows;
        }

        public static string ConvertToBaixa(string rawText, string codFilial, string codTipoDoc, string codContaCaixa, string idFormaPgto)
        {
            if (string.IsNullOrEmpty(rawText)) return "";

            string[] rawLines = rawText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> lines = new List<string>();
            foreach (string l in rawLines)
            {
                string t = l.TrimEnd();
                if (!string.IsNullOrEmpty(t)) lines.Add(t);
            }

            if (lines.Count == 0) return "";

            // 0. Verifica se o arquivo já é um layout de Baixa TOTVS RM (Linha L, >= 900 posições)
            if (IsBaixaRmLayout(lines))
            {
                return ProcessBaixaRmFile(lines, codFilial, codTipoDoc, codContaCaixa, idFormaPgto);
            }

            // 1. Tenta CNAB 240 (Segmentos A+B ou T+U)
            bool isCnab240 = false;
            foreach (string l in lines)
            {
                if (l.Length >= 14 && l.Substring(7, 1) == "3")
                {
                    isCnab240 = true;
                    break;
                }
            }

            if (isCnab240)
            {
                string res = ConvertCnab240(lines, codFilial, codTipoDoc, codContaCaixa, idFormaPgto);
                if (!string.IsNullOrEmpty(res)) return res;
            }

            // 2. Tenta CNAB 400 (Linha detalhe inicia com '1' e comprimento >= 400)
            bool isCnab400 = false;
            foreach (string l in lines)
            {
                if (l.Length >= 400 && l.StartsWith("1"))
                {
                    isCnab400 = true;
                    break;
                }
            }

            if (isCnab400)
            {
                string res = ConvertCnab400(lines, codFilial, codTipoDoc, codContaCaixa, idFormaPgto);
                if (!string.IsNullOrEmpty(res)) return res;
            }

            // 3. Fallback: Arquivo delimitado tabular (CSV / TXT)
            char delim = CsvToOfxConverter.DetectDelimiter(rawText);
            List<List<string>> rows = CsvToOfxConverter.ParseCsv(rawText, delim);
            return ConvertTableToBaixa(rows, codFilial, codTipoDoc, codContaCaixa, idFormaPgto);
        }

        private static string ConvertCnab240(List<string> lines, string codFilial, string codTipoDoc, string codContaCaixa, string idFormaPgto)
        {
            List<string> resultLines = new List<string>();

            // Extrai informações do Header de Arquivo ou Lote
            string cnabBanco = "";
            string cnabAg = "";
            string cnabConta = "";
            string cnabNomeBanco = "";

            foreach (string l in lines)
            {
                if (l.Length >= 14 && l.Substring(7, 1) == "0") // Header de Arquivo
                {
                    cnabBanco = l.Substring(0, 3).Trim();
                    if (l.Length >= 57) cnabAg = l.Substring(52, 5).Trim();
                    if (l.Length >= 70) cnabConta = l.Substring(58, 12).Trim();
                    if (l.Length >= 132) cnabNomeBanco = l.Substring(102, 30).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(cnabBanco))
            {
                foreach (string l in lines)
                {
                    if (l.Length >= 14 && l.Substring(7, 1) == "1") // Header de Lote
                    {
                        cnabBanco = l.Substring(0, 3).Trim();
                        if (l.Length >= 57) cnabAg = l.Substring(52, 5).Trim();
                        if (l.Length >= 70) cnabConta = l.Substring(58, 12).Trim();
                        break;
                    }
                }
            }

            var cxaRes = TotvsDbService.ResolveContaCaixa(cnabBanco, cnabAg, cnabConta, cnabNomeBanco);
            string effectiveContaCaixa = !string.IsNullOrEmpty(cxaRes.CodCxa) ? cxaRes.CodCxa : (string.IsNullOrEmpty(codContaCaixa) || codContaCaixa == "1" ? "30" : codContaCaixa);
            string effectiveNomeBanco = !string.IsNullOrEmpty(cxaRes.NomeBanco) ? cxaRes.NomeBanco : cnabNomeBanco;
            if (string.IsNullOrEmpty(effectiveNomeBanco))
            {
                effectiveNomeBanco = TotvsDbService.ExtractNomeBanco("", "", cnabBanco);
            }
            string effectiveTipoDoc = (!string.IsNullOrEmpty(codTipoDoc) && codTipoDoc != "ICOP" && codTipoDoc != "CRMD") ? codTipoDoc : "SALP";

            // Busca pares Segmento A + Segmento B (Pagamento a Fornecedores / Favorecidos)
            string curA = null;
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.Length >= 14 && l.Substring(7, 1) == "3")
                {
                    string seg = l.Substring(13, 1);
                    if (seg == "A")
                    {
                        curA = l;
                    }
                    else if (seg == "B" && curA != null)
                    {
                        string lineBx = BuildFromSegmentAB(curA, l, codFilial, effectiveTipoDoc, effectiveContaCaixa, idFormaPgto, effectiveNomeBanco);
                        resultLines.Add(lineBx);
                        curA = null;
                    }
                }
            }

            // Se não encontrou pares A+B, busca pares T+U (Cobrança Bancária / Recebimento)
            if (resultLines.Count == 0)
            {
                string curT = null;
                for (int i = 0; i < lines.Count; i++)
                {
                    string l = lines[i];
                    if (l.Length >= 14 && l.Substring(7, 1) == "3")
                    {
                        string seg = l.Substring(13, 1);
                        if (seg == "T")
                        {
                            curT = l;
                        }
                        else if (seg == "U" && curT != null)
                        {
                            string lineBx = BuildFromSegmentTU(curT, l, codFilial, effectiveTipoDoc, effectiveContaCaixa, idFormaPgto, effectiveNomeBanco);
                            resultLines.Add(lineBx);
                            curT = null;
                        }
                    }
                }
            }

            // Se ainda não encontrou pares, processa Segmentos A individuais
            if (resultLines.Count == 0)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    string l = lines[i];
                    if (l.Length >= 14 && l.Substring(7, 1) == "3" && l.Substring(13, 1) == "A")
                    {
                        string lineBx = BuildFromSegmentAB(l, null, codFilial, effectiveTipoDoc, effectiveContaCaixa, idFormaPgto, effectiveNomeBanco);
                        resultLines.Add(lineBx);
                    }
                }
            }

            if (resultLines.Count == 0) return "";
            return string.Join("\r\n", resultLines.ToArray()) + "\r\n";
        }

        private static string BuildFromSegmentAB(string segA, string segB, string codFilial, string codTipoDoc, string codContaCaixa, string idFormaPgto, string bancoNome)
        {
            // Segmento A: Dados do favorecido, documento, data e valor
            string numDoc = segA.Length >= 93 ? segA.Substring(73, Math.Min(20, segA.Length - 73)).Trim() : "";
            string nossoNum = segA.Length >= 154 ? segA.Substring(134, Math.Min(20, segA.Length - 134)).Trim() : "";
            string dtPgto = segA.Length >= 162 ? segA.Substring(154, 8).Trim() : (segA.Length >= 101 ? segA.Substring(93, 8).Trim() : "");
            string dtBaixa6 = "";
            if (dtPgto.Length == 8)
            {
                dtBaixa6 = dtPgto.Substring(0, 4) + dtPgto.Substring(6, 2);
            }
            else if (dtPgto.Length == 6)
            {
                dtBaixa6 = dtPgto;
            }
            else
            {
                dtBaixa6 = DateTime.Now.ToString("ddMMyy");
            }

            long cents = 0;
            if (segA.Length >= 177)
            {
                long.TryParse(segA.Substring(162, 15).Trim(), out cents);
            }
            if (cents == 0 && segA.Length >= 134)
            {
                long.TryParse(segA.Substring(119, 15).Trim(), out cents);
            }
            decimal vlrReal = (decimal)cents / 100m;

            string favorecido = segA.Length >= 73 ? segA.Substring(43, Math.Min(30, segA.Length - 43)).Trim() : "";

            // Segmento B: CPF/CNPJ e valores adicionais (desconto, juros, multa)
            string cpfCnpj = "";
            decimal juros = 0m;
            decimal desconto = 0m;
            decimal multa = 0m;

            if (segB != null)
            {
                string tipoInsc = segB.Length >= 18 ? segB.Substring(17, 1) : "1";
                string rawDoc = segB.Length >= 32 ? segB.Substring(18, 14).Trim() : "";
                if (tipoInsc == "1" && rawDoc.Length >= 11)
                {
                    cpfCnpj = rawDoc.Substring(rawDoc.Length - 11);
                }
                else
                {
                    cpfCnpj = rawDoc;
                }

                if (segB.Length >= 180) desconto = ParseMoneyCents(segB.Substring(165, 15));
                if (segB.Length >= 195) juros = ParseMoneyCents(segB.Substring(180, 15));
                if (segB.Length >= 210) multa = ParseMoneyCents(segB.Substring(195, 15));

                if (string.IsNullOrEmpty(numDoc) && segB.Length >= 225)
                {
                    numDoc = segB.Substring(210, 15).Trim();
                }
            }

            // Realiza consulta automática ao banco TOTVS RM na tabela FLAN com prioridade STATUSLAN = 0
            var flan = TotvsDbService.QueryLancamento(numDoc, cpfCnpj, favorecido, vlrReal);
            if (!flan.Found && !string.IsNullOrEmpty(nossoNum))
            {
                flan = TotvsDbService.QueryLancamento(nossoNum, "", "", vlrReal);
            }

            string defaultTipoDoc = (!string.IsNullOrEmpty(codTipoDoc) && codTipoDoc != "ICOP" && codTipoDoc != "CRMD") ? codTipoDoc : "SALP";
            string finalTipoDoc = (flan.Found && !string.IsNullOrEmpty(flan.CodTipoDoc)) ? flan.CodTipoDoc : defaultTipoDoc;
            string finalFilial = (flan.Found && !string.IsNullOrEmpty(flan.CodFilial)) ? flan.CodFilial : codFilial;
            string defaultCliFor = (!string.IsNullOrEmpty(bancoNome) && bancoNome.ToUpperInvariant().Contains("SICOOB"))
                ? "F1000706904"
                : (!string.IsNullOrEmpty(bancoNome) ? bancoNome : (!string.IsNullOrEmpty(cpfCnpj) ? cpfCnpj : favorecido));
            string finalCliFor = (flan.Found && !string.IsNullOrEmpty(flan.CodCfo)) ? flan.CodCfo : defaultCliFor;
            string finalContaCaixa = !string.IsNullOrEmpty(codContaCaixa) ? codContaCaixa : (flan.Found && !string.IsNullOrEmpty(flan.CodConta)) ? flan.CodConta : "30";
            string finalNumDoc = (flan.Found && !string.IsNullOrEmpty(flan.NumeroDocumento)) ? flan.NumeroDocumento : numDoc;
            string histDoc = !string.IsNullOrEmpty(numDoc) ? numDoc : finalNumDoc;
            string idLan = flan.Found ? flan.IdLan : "";
            if (vlrReal == 0m && flan.Found && flan.ValorOriginal > 0m)
            {
                vlrReal = flan.ValorOriginal;
            }

            string numFp = NormalizeIdFormaPgto(idFormaPgto);
            if (string.IsNullOrEmpty(numFp) || numFp == "12")
            {
                numFp = "3";
            }

            // Consulta nome do funcionário no banco TOTVS RM (PFUNC) com base na chapa contida no número do documento
            string nomeFunc = TotvsDbService.QueryFuncionarioByChapa(numDoc, cpfCnpj);
            if (string.IsNullOrEmpty(nomeFunc))
            {
                nomeFunc = favorecido;
            }

            string comp = TotvsDbService.ResolveCompetencia(flan.Competencia, numDoc, dtBaixa6);

            return BuildBaixaLine(
                finalFilial,
                finalCliFor,
                finalTipoDoc,
                finalNumDoc,
                dtBaixa6,
                vlrReal,
                juros,
                desconto,
                multa,
                finalContaCaixa,
                favorecido,
                numFp,
                GetFormaPgtoNome(numFp),
                idLan,
                bancoNome,
                histDoc,
                nomeFunc,
                comp
            );
        }

        private static string BuildFromSegmentTU(string segT, string segU, string codFilial, string codTipoDoc, string codContaCaixa, string idFormaPgto, string bancoNome)
        {
            // Segmento T: Nosso Número e Valor Nominal
            string numDoc = "";
            if (segT.Length >= 73)
            {
                numDoc = segT.Substring(58, Math.Min(15, segT.Length - 58)).Trim();
            }
            if (string.IsNullOrEmpty(numDoc) && segT.Length >= 88)
            {
                numDoc = segT.Substring(73, Math.Min(15, segT.Length - 73)).Trim();
            }

            decimal vlrNominal = 0m;
            if (segT.Length >= 96)
            {
                vlrNominal = ParseMoneyCents(segT.Substring(81, 15));
            }

            // Segmento U: Valores Efetivos e Data
            decimal juros = 0m;
            decimal desconto = 0m;
            decimal vlrPago = vlrNominal;
            string dtPgto = "";

            if (segU != null)
            {
                if (segU.Length >= 32) juros = ParseMoneyCents(segU.Substring(17, 15));
                if (segU.Length >= 47) desconto = ParseMoneyCents(segU.Substring(32, 15));
                if (segU.Length >= 92)
                {
                    decimal vp = ParseMoneyCents(segU.Substring(77, 15));
                    if (vp > 0) vlrPago = vp;
                }

                if (segU.Length >= 145) dtPgto = segU.Substring(137, 8).Trim();
                if (string.IsNullOrEmpty(dtPgto) && segU.Length >= 153) dtPgto = segU.Substring(145, 8).Trim();
            }

            string dtBaixa6 = "";
            if (dtPgto.Length == 8)
            {
                dtBaixa6 = dtPgto.Substring(0, 4) + dtPgto.Substring(6, 2);
            }
            else if (dtPgto.Length == 6)
            {
                dtBaixa6 = dtPgto;
            }
            else
            {
                dtBaixa6 = DateTime.Now.ToString("ddMMyy");
            }

            // Consulta automática ao banco TOTVS RM na tabela FLAN com prioridade STATUSLAN = 0
            var flan = TotvsDbService.QueryLancamento(numDoc, "", vlrPago);

            string defaultTipoDoc = (!string.IsNullOrEmpty(codTipoDoc) && codTipoDoc != "ICOP" && codTipoDoc != "CRMD") ? codTipoDoc : "SALP";
            string finalTipoDoc = (flan.Found && !string.IsNullOrEmpty(flan.CodTipoDoc)) ? flan.CodTipoDoc : defaultTipoDoc;
            string finalFilial = (flan.Found && !string.IsNullOrEmpty(flan.CodFilial)) ? flan.CodFilial : codFilial;
            string defaultCliFor = (!string.IsNullOrEmpty(bancoNome) && bancoNome.ToUpperInvariant().Contains("SICOOB"))
                ? "F1000706904"
                : (!string.IsNullOrEmpty(bancoNome) ? bancoNome : "");
            string finalCliFor = (!string.IsNullOrEmpty(flan.CodCfo)) ? flan.CodCfo : defaultCliFor;
            string finalContaCaixa = !string.IsNullOrEmpty(codContaCaixa) ? codContaCaixa : (flan.Found && !string.IsNullOrEmpty(flan.CodConta)) ? flan.CodConta : "30";
            string finalNumDoc = (flan.Found && !string.IsNullOrEmpty(flan.NumeroDocumento)) ? flan.NumeroDocumento : numDoc;
            string histDoc = !string.IsNullOrEmpty(numDoc) ? numDoc : finalNumDoc;
            string idLan = flan.Found ? flan.IdLan : "";

            string numFp = NormalizeIdFormaPgto(idFormaPgto);
            if (string.IsNullOrEmpty(numFp) || numFp == "12")
            {
                numFp = "3";
            }

            string nomeFuncTU = TotvsDbService.QueryFuncionarioByChapa(numDoc, "");
            string compTU = TotvsDbService.ResolveCompetencia(flan.Competencia, numDoc, dtBaixa6);

            return BuildBaixaLine(
                finalFilial,
                finalCliFor,
                finalTipoDoc,
                finalNumDoc,
                dtBaixa6,
                vlrPago,
                juros,
                desconto,
                0m,
                finalContaCaixa,
                "",
                numFp,
                GetFormaPgtoNome(numFp),
                idLan,
                bancoNome,
                histDoc,
                nomeFuncTU,
                compTU
            );
        }

        private static string ConvertCnab400(List<string> lines, string codFilial, string codTipoDoc, string codContaCaixa, string idFormaPgto)
        {
            List<string> resultLines = new List<string>();

            // Extrai dados bancários do Header CNAB 400
            string cnabBanco = "";
            string cnabAg = "";
            string cnabConta = "";
            string cnabNome = "";

            foreach (string l in lines)
            {
                if (l.Length >= 400 && l.StartsWith("0"))
                {
                    if (l.Length >= 79) cnabBanco = l.Substring(76, 3).Trim();
                    if (l.Length >= 30) cnabAg = l.Substring(26, 4).Trim();
                    if (l.Length >= 39) cnabConta = l.Substring(31, 8).Trim();
                    if (l.Length >= 94) cnabNome = l.Substring(79, 15).Trim();
                    break;
                }
            }

            var cxaRes = TotvsDbService.ResolveContaCaixa(cnabBanco, cnabAg, cnabConta, cnabNome);
            string effectiveContaCaixa = !string.IsNullOrEmpty(cxaRes.CodCxa) ? cxaRes.CodCxa : (string.IsNullOrEmpty(codContaCaixa) || codContaCaixa == "1" ? "30" : codContaCaixa);
            string effectiveNomeBanco = !string.IsNullOrEmpty(cxaRes.NomeBanco) ? cxaRes.NomeBanco : cnabNome;
            if (string.IsNullOrEmpty(effectiveNomeBanco))
            {
                effectiveNomeBanco = TotvsDbService.ExtractNomeBanco("", "", cnabBanco);
            }
            string effectiveTipoDoc = (!string.IsNullOrEmpty(codTipoDoc) && codTipoDoc != "ICOP" && codTipoDoc != "CRMD") ? codTipoDoc : "SALP";

            foreach (string l in lines)
            {
                if (l.Length >= 400 && l.StartsWith("1"))
                {
                    string numDoc = "";
                    if (l.Length >= 126) numDoc = l.Substring(116, 10).Trim();
                    if (string.IsNullOrEmpty(numDoc) && l.Length >= 126) numDoc = l.Substring(108, 18).Trim();

                    string dtOcorr = l.Length >= 116 ? l.Substring(110, 6).Trim() : DateTime.Now.ToString("ddMMyy");
                    decimal vlrPago = l.Length >= 165 ? ParseMoneyCents(l.Substring(152, 13)) : 0m;
                    string cpfCnpj = l.Length >= 232 ? l.Substring(218, 14).Trim() : "";

                    var flan = TotvsDbService.QueryLancamento(numDoc, cpfCnpj, vlrPago);

                    string finalTipoDoc = (flan.Found && !string.IsNullOrEmpty(flan.CodTipoDoc)) ? flan.CodTipoDoc : effectiveTipoDoc;
                    string finalFilial = (flan.Found && !string.IsNullOrEmpty(flan.CodFilial)) ? flan.CodFilial : codFilial;
                    string defaultCliFor = (!string.IsNullOrEmpty(effectiveNomeBanco) && effectiveNomeBanco.ToUpperInvariant().Contains("SICOOB"))
                        ? "F1000706904"
                        : (!string.IsNullOrEmpty(effectiveNomeBanco) ? effectiveNomeBanco : cpfCnpj);
                    string finalCliFor = (!string.IsNullOrEmpty(flan.CodCfo)) ? flan.CodCfo : defaultCliFor;
                    string finalContaCaixa = !string.IsNullOrEmpty(effectiveContaCaixa) ? effectiveContaCaixa : (flan.Found && !string.IsNullOrEmpty(flan.CodConta)) ? flan.CodConta : "30";
                    string idLan = flan.Found ? flan.IdLan : "";

                    string numFp = NormalizeIdFormaPgto(idFormaPgto);
                    if (string.IsNullOrEmpty(numFp) || numFp == "12")
                    {
                        numFp = "3";
                    }

                    string nomeFunc400 = TotvsDbService.QueryFuncionarioByChapa(numDoc, "");
                    string comp400 = TotvsDbService.ResolveCompetencia(flan.Competencia, numDoc, dtOcorr);

                    string lineBx = BuildBaixaLine(
                        finalFilial,
                        finalCliFor,
                        finalTipoDoc,
                        numDoc,
                        dtOcorr,
                        vlrPago,
                        0m,
                        0m,
                        0m,
                        finalContaCaixa,
                        "",
                        numFp,
                        GetFormaPgtoNome(numFp),
                        idLan,
                        effectiveNomeBanco,
                        null,
                        nomeFunc400,
                        comp400
                    );
                    resultLines.Add(lineBx);
                }
            }
            if (resultLines.Count == 0) return "";
            return string.Join("\r\n", resultLines.ToArray()) + "\r\n";
        }

        public static string ConvertTableToBaixa(List<List<string>> rows, string codFilial, string codTipoDoc, string codContaCaixa, string idFormaPgto)
        {
            if (rows == null || rows.Count == 0) return "";

            List<string> headers = rows[0];
            int dateCol = -1, descCol = -1, amountCol = -1, docCol = -1, cpfCol = -1;
            int filCol = -1, tdCol = -1, ccCol = -1, fpCol = -1, idLanCol = -1;
            int compCol = -1, nomeCol = -1, bancoCol = -1, cxaDescCol = -1;

            string[] dateAliases = new string[] { "data", "date", "dt", "lancamento", "transacao", "vencimento", "pagamento" };
            string[] descAliases = new string[] { "historico", "descricao", "memo", "description", "detalhe", "favorecido", "nome", "cliente", "fornecedor" };
            string[] amountAliases = new string[] { "valor", "amount", "quantia", "total", "liquido", "debito", "credito", "pago" };
            string[] docAliases = new string[] { "documento", "doc", "identificador", "numdoc", "numero", "ndoc", "titulo", "cheque" };
            string[] cpfAliases = new string[] { "cpf", "cnpj", "inscricao", "documento_favorecido", "codclifor", "cpf_cnpj" };

            string[] filAliases = new string[] { "filial", "codfilial", "cod_filial" };
            string[] tdAliases = new string[] { "tipodoc", "tipo_doc", "codtipodoc", "cod_tipo_doc", "tipo documento", "tipo de documento", "documento tipo" };
            string[] ccAliases = new string[] { "contacaixa", "conta_caixa", "codconta", "cod_conta", "codcontacaixa", "conta caixa", "conta" };
            string[] fpAliases = new string[] { "formapgto", "forma_pgto", "idformapgto", "id_forma_pgto", "forma pagamento", "forma de pagamento" };
            string[] idLanAliases = new string[] { "idlan", "id_lan", "id lancamento", "idlancamento" };

            // 1. Detect if this is a TOTVS RM FLAN grid copy/export
            bool isRmFlanGrid = false;
            if (headers != null && headers.Count >= 15)
            {
                string c0 = headers[0].Trim().ToLowerInvariant();
                bool hasRmCfo = (headers.Count > 6 && (headers[6].StartsWith("L") || headers[6].StartsWith("F") || headers[6].StartsWith("C")));
                bool hasIdLan = (headers.Count > 3 && TotvsDbService.IsAllDigits(headers[3]) && headers[3].Length >= 4);
                if (c0.Contains("selecionado") || (hasRmCfo && hasIdLan))
                {
                    isRmFlanGrid = true;
                }
            }

            int startRow = 1;
            if (isRmFlanGrid)
            {
                // In RM FLAN grid, columns have well-defined positions:
                filCol = 1;
                idLanCol = 3;
                cpfCol = 6;
                tdCol = 7;
                docCol = 8;
                compCol = 11;
                dateCol = 13; // Data Baixa/Previsão (fallback to 10 Data Vencimento)
                amountCol = 14;
                descCol = 18;
                ccCol = 27;
                cxaDescCol = 28;
                nomeCol = 86;
                bancoCol = 132;

                // Check if row 0 is a header or data
                string c3 = (headers.Count > 3) ? headers[3].Trim() : "";
                if (TotvsDbService.IsAllDigits(c3))
                {
                    startRow = 0; // Row 0 is already a data row!
                }
                else
                {
                    startRow = 1;
                }
            }
            else
            {
                bool hasHeader = false;
                for (int i = 0; i < headers.Count; i++)
                {
                    string norm = CsvToOfxConverter.Normalize(headers[i]);
                    if (dateCol == -1 && CsvToOfxConverter.MatchAny(norm, dateAliases)) { dateCol = i; hasHeader = true; }
                    else if (descCol == -1 && CsvToOfxConverter.MatchAny(norm, descAliases)) { descCol = i; hasHeader = true; }
                    else if (amountCol == -1 && CsvToOfxConverter.MatchAny(norm, amountAliases)) { amountCol = i; hasHeader = true; }
                    else if (docCol == -1 && CsvToOfxConverter.MatchAny(norm, docAliases)) { docCol = i; hasHeader = true; }
                    else if (cpfCol == -1 && CsvToOfxConverter.MatchAny(norm, cpfAliases)) { cpfCol = i; hasHeader = true; }

                    if (filCol == -1 && CsvToOfxConverter.MatchAny(norm, filAliases)) { filCol = i; hasHeader = true; }
                    else if (tdCol == -1 && CsvToOfxConverter.MatchAny(norm, tdAliases)) { tdCol = i; hasHeader = true; }
                    else if (ccCol == -1 && CsvToOfxConverter.MatchAny(norm, ccAliases)) { ccCol = i; hasHeader = true; }
                    else if (fpCol == -1 && CsvToOfxConverter.MatchAny(norm, fpAliases)) { fpCol = i; hasHeader = true; }
                    else if (idLanCol == -1 && CsvToOfxConverter.MatchAny(norm, idLanAliases)) { idLanCol = i; hasHeader = true; }
                }

                if (!hasHeader)
                {
                    startRow = 0;
                    if (dateCol == -1) dateCol = 0;
                    if (amountCol == -1) amountCol = (headers.Count > 1 ? 1 : 0);
                }
                else
                {
                    if (dateCol == -1) dateCol = 0;
                    if (amountCol == -1) amountCol = (headers.Count > 1 ? 1 : 0);
                    startRow = 1;
                }
            }

            List<string> resultLines = new List<string>();

            for (int r = startRow; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row == null || row.Count == 0) continue;

                string rawDate = (dateCol < row.Count) ? row[dateCol].Trim() : "";
                if (string.IsNullOrEmpty(rawDate) && isRmFlanGrid && row.Count > 10)
                {
                    rawDate = row[10].Trim(); // fallback to Data Vencimento
                }
                string rawDesc = (descCol >= 0 && descCol < row.Count) ? row[descCol].Trim() : "";
                string rawAmount = (amountCol < row.Count) ? row[amountCol].Trim() : "";
                string rawDoc = (docCol >= 0 && docCol < row.Count) ? row[docCol].Trim() : "";
                string rawCpf = (cpfCol >= 0 && cpfCol < row.Count) ? row[cpfCol].Trim() : "";
                string rawIdLan = (idLanCol >= 0 && idLanCol < row.Count) ? row[idLanCol].Trim() : "";

                string rowFil = (filCol >= 0 && filCol < row.Count && !string.IsNullOrEmpty(row[filCol].Trim())) ? row[filCol].Trim() : codFilial;
                string rowTd = (tdCol >= 0 && tdCol < row.Count && !string.IsNullOrEmpty(row[tdCol].Trim())) ? row[tdCol].Trim() : null;
                string rowCc = (ccCol >= 0 && ccCol < row.Count && !string.IsNullOrEmpty(row[ccCol].Trim())) ? row[ccCol].Trim() : codContaCaixa;
                string rowFp = (fpCol >= 0 && fpCol < row.Count && !string.IsNullOrEmpty(row[fpCol].Trim())) ? row[fpCol].Trim() : idFormaPgto;
                rowFp = NormalizeIdFormaPgto(rowFp);

                string rowComp = (compCol >= 0 && compCol < row.Count) ? row[compCol].Trim() : "";
                string rowNome = (nomeCol >= 0 && nomeCol < row.Count) ? row[nomeCol].Trim() : "";
                string rowBancoCode = (bancoCol >= 0 && bancoCol < row.Count) ? row[bancoCol].Trim() : "";
                string rowCxaDesc = (cxaDescCol >= 0 && cxaDescCol < row.Count) ? row[cxaDescCol].Trim() : "";

                if (string.IsNullOrEmpty(rawDate) && string.IsNullOrEmpty(rawAmount)) continue;

                string dtStr = CsvToOfxConverter.ParseDate(rawDate);
                string dtBaixa6 = "";
                if (!string.IsNullOrEmpty(dtStr) && dtStr.Length == 8)
                {
                    dtBaixa6 = dtStr.Substring(6, 2) + dtStr.Substring(4, 2) + dtStr.Substring(2, 2);
                }
                else
                {
                    dtBaixa6 = DateTime.Now.ToString("ddMMyy");
                }

                decimal val = Math.Abs(CsvToOfxConverter.ParseAmount(rawAmount));

                if (string.IsNullOrEmpty(rawDoc))
                {
                    rawDoc = r.ToString();
                }

                var flan = TotvsDbService.QueryLancamento(rawDoc, rawCpf, rawDesc, val);
                if (flan.Found)
                {
                    if (string.IsNullOrEmpty(rowTd) && !string.IsNullOrEmpty(flan.CodTipoDoc)) rowTd = flan.CodTipoDoc;
                    if (string.IsNullOrEmpty(rawIdLan)) rawIdLan = flan.IdLan;
                    if (!string.IsNullOrEmpty(flan.CodFilial)) rowFil = flan.CodFilial;
                    if (!string.IsNullOrEmpty(flan.CodConta)) rowCc = flan.CodConta;
                    if (!string.IsNullOrEmpty(flan.CodCfo)) rawCpf = flan.CodCfo;
                }
                else
                {
                    if (!string.IsNullOrEmpty(flan.CodCfo)) rawCpf = flan.CodCfo;
                }

                if (string.IsNullOrEmpty(rowTd)) rowTd = codTipoDoc;

                string nomeFuncTable = rowNome;
                if (string.IsNullOrEmpty(nomeFuncTable))
                {
                    nomeFuncTable = TotvsDbService.QueryFuncionarioByChapa(rawDoc, rawCpf);
                }
                if (string.IsNullOrEmpty(nomeFuncTable))
                {
                    nomeFuncTable = rawDesc;
                }

                string compTable = rowComp;
                if (string.IsNullOrEmpty(compTable))
                {
                    compTable = TotvsDbService.ResolveCompetencia(flan.Competencia, rawDoc, dtBaixa6);
                }

                string bancoNome = "";
                if (rowBancoCode == "756" || rowCxaDesc.ToUpper().Contains("SICOOB"))
                {
                    bancoNome = "SICOOB";
                }
                else if (rowBancoCode == "001" || rowBancoCode == "1" || rowCxaDesc.ToUpper().Contains("BRASIL"))
                {
                    bancoNome = "BANCO DO BRASIL";
                }
                else if (rowBancoCode == "237" || rowCxaDesc.ToUpper().Contains("BRADESCO"))
                {
                    bancoNome = "BRADESCO";
                }
                else if (rowBancoCode == "104" || rowCxaDesc.ToUpper().Contains("CAIXA"))
                {
                    bancoNome = "CAIXA";
                }
                else if (rowBancoCode == "033" || rowBancoCode == "33" || rowCxaDesc.ToUpper().Contains("SANTANDER"))
                {
                    bancoNome = "SANTANDER";
                }
                else if (!string.IsNullOrEmpty(rowBancoCode))
                {
                    bancoNome = TotvsDbService.ExtractNomeBanco("", "", rowBancoCode);
                }
                if (string.IsNullOrEmpty(bancoNome))
                {
                    bancoNome = "SICOOB";
                }

                string lineBx = BuildBaixaLine(
                    rowFil,
                    rawCpf,
                    rowTd,
                    rawDoc,
                    dtBaixa6,
                    val,
                    0m,
                    0m,
                    0m,
                    rowCc,
                    "",
                    rowFp,
                    GetFormaPgtoNome(rowFp),
                    rawIdLan,
                    bancoNome,
                    rawDoc,
                    nomeFuncTable,
                    compTable
                );
                resultLines.Add(lineBx);
            }

            if (resultLines.Count == 0) return "";
            return string.Join("\r\n", resultLines.ToArray()) + "\r\n";
        }

        private static decimal ParseMoneyCents(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0m;
            long val;
            if (long.TryParse(str.Trim(), out val))
            {
                return (decimal)val / 100m;
            }
            return 0m;
        }

        public static string FormatMoney(decimal value)
        {
            long intPart = (long)Math.Truncate(Math.Abs(value));
            long decPart = (long)Math.Round((Math.Abs(value) - intPart) * 10000m);
            return string.Format("{0:D13}.{1:D4}", intPart, decPart);
        }

        public static string BuildBaixaLineDirect(
            string codFilial,
            string codCliFor,
            string codTipoDoc,
            string numDoc,
            string dtBaixa6,
            decimal vlrBaixado,
            decimal vlrJuros,
            decimal vlrDesconto,
            decimal vlrMulta,
            string codContaCaixa,
            string customHist,
            string idFormaPgto,
            string idLan)
        {
            string f_tipo_linha = "L";
            string f_cod_filial = (codFilial ?? "0001").PadLeft(4, '0').Substring(0, 4);
            string f_cod_clifor = (codCliFor ?? "").PadRight(25, ' ').Substring(0, 25);
            string f_cod_tipo_doc = (codTipoDoc ?? "SALP").PadRight(10, ' ').Substring(0, 10);
            string f_num_doc = (numDoc ?? "").PadRight(40, ' ').Substring(0, 40);
            string f_dt_baixa = (dtBaixa6 ?? "").PadRight(6, ' ').Substring(0, 6);
            string f_vlr_bx = FormatMoney(vlrBaixado);
            string f_vlr_jr = FormatMoney(vlrJuros);
            string f_vlr_desc = FormatMoney(vlrDesconto);
            string f_cod_col_cx = "0000";
            string f_cod_cta_cx = (codContaCaixa ?? "30").PadRight(10, ' ').Substring(0, 10);
            string f_num_cont = new string(' ', 20);
            string f_eve_cont = new string(' ', 5);
            string f_cod_col_clifor = "0000";
            string f_ser_doc1 = new string(' ', 3);

            StringBuilder sbOpc = new StringBuilder(144);
            for (int i = 0; i < 8; i++) sbOpc.Append(FormatMoney(0m));
            string f_vlr_opc = sbOpc.ToString();

            string f_vlr_multa = FormatMoney(vlrMulta);
            string f_reutil = "0000";
            string f_num_cheque = new string(' ', 20);

            string histText = customHist;
            if (string.IsNullOrEmpty(histText))
            {
                CultureInfo ptBr = new CultureInfo("pt-BR");
                string vlrStr = vlrBaixado.ToString("N2", ptBr);
                histText = string.Format("Baixa Filial: 1 - Forma de Pagamento: {0} - Valor: R${1} - Número do Documento: {2}",
                    GetFormaPgtoNome(idFormaPgto),
                    vlrStr,
                    numDoc
                );
            }
            if (!string.IsNullOrEmpty(idLan) && !histText.Contains("IDLAN"))
            {
                histText += " - IDLAN: " + idLan;
            }
            string f_hist_baixa = histText.PadRight(255, ' ');
            if (f_hist_baixa.Length > 255) f_hist_baixa = f_hist_baixa.Substring(0, 255);

            string f_tab_opc = new string(' ', 125);
            string f_camp_alfa1 = (idLan ?? "").PadRight(100, ' ').Substring(0, 100);
            string f_camp_alfa2_3 = new string(' ', 40);
            string f_dt_opc = new string(' ', 30);
            string f_ser_doc2 = new string(' ', 8);
            string numFp = NormalizeIdFormaPgto(idFormaPgto);
            string f_id_forma_pgto = numFp.PadRight(7, ' ').Substring(0, 7);

            return string.Concat(
                f_tipo_linha, f_cod_filial, f_cod_clifor, f_cod_tipo_doc, f_num_doc, f_dt_baixa,
                f_vlr_bx, f_vlr_jr, f_vlr_desc, f_cod_col_cx, f_cod_cta_cx,
                f_num_cont, f_eve_cont, f_cod_col_clifor, f_ser_doc1,
                f_vlr_opc, f_vlr_multa, f_reutil, f_num_cheque, f_hist_baixa,
                f_tab_opc, f_camp_alfa1, f_camp_alfa2_3, f_dt_opc, f_ser_doc2, f_id_forma_pgto
            );
        }

        public static string BuildBaixaLineDirect(
            string codFilial,
            string codCliFor,
            string codTipoDoc,
            string numDoc,
            string dtBaixa6,
            decimal vlrBaixado,
            decimal vlrJuros,
            decimal vlrDesconto,
            decimal vlrMulta,
            string codContaCaixa,
            string customHist,
            string idFormaPgto)
        {
            return BuildBaixaLineDirect(codFilial, codCliFor, codTipoDoc, numDoc, dtBaixa6, vlrBaixado, vlrJuros, vlrDesconto, vlrMulta, codContaCaixa, customHist, idFormaPgto, "");
        }

        public static string BuildBaixaLine(
            string codFilial,
            string codCliFor,
            string codTipoDoc,
            string numDoc,
            string dtBaixa6,
            decimal vlrBaixado,
            decimal vlrJuros,
            decimal vlrDesconto,
            decimal vlrMulta,
            string codContaCaixa,
            string favorecidoNome,
            string idFormaPgto,
            string formaPgtoNome,
            string idLan,
            string bancoNome,
            string histDoc = null,
            string funcionarioNome = null,
            string competencia = null)
        {
            CultureInfo ptBr = new CultureInfo("pt-BR");
            string vlrStr = vlrBaixado.ToString("N2", ptBr);
            string nomeFp = !string.IsNullOrEmpty(formaPgtoNome) ? formaPgtoNome : GetFormaPgtoNome(idFormaPgto);
            string docForHist = !string.IsNullOrEmpty(histDoc) ? histDoc : numDoc;
            string histText = string.Format("Baixa Filial: 1 - Forma de Pagamento: {0} - Valor: R${1} - Número do Documento: {2}",
                nomeFp,
                vlrStr,
                docForHist
            );

            string compEfetiva = !string.IsNullOrEmpty(competencia) ? competencia : TotvsDbService.ResolveCompetencia("", docForHist, dtBaixa6);
            if (!string.IsNullOrEmpty(compEfetiva))
            {
                histText += " - Competência: " + compEfetiva;
            }

            string favEfetivo = !string.IsNullOrEmpty(bancoNome) ? bancoNome : favorecidoNome;
            string funcEfetivo = !string.IsNullOrEmpty(funcionarioNome) ? funcionarioNome : favorecidoNome;

            if (!string.IsNullOrEmpty(favEfetivo))
            {
                if (!string.IsNullOrEmpty(funcEfetivo) && !favEfetivo.Equals(funcEfetivo, StringComparison.OrdinalIgnoreCase))
                {
                    histText += string.Format(" - Favorecido: {0} - {1}", favEfetivo, funcEfetivo);
                }
                else
                {
                    histText += " - Favorecido: " + favEfetivo;
                }
            }
            else if (!string.IsNullOrEmpty(funcEfetivo))
            {
                histText += " - Favorecido: " + funcEfetivo;
            }

            return BuildBaixaLineDirect(codFilial, codCliFor, codTipoDoc, numDoc, dtBaixa6, vlrBaixado, vlrJuros, vlrDesconto, vlrMulta, codContaCaixa, histText, idFormaPgto, "");
        }

        public static string BuildBaixaLine(
            string codFilial,
            string codCliFor,
            string codTipoDoc,
            string numDoc,
            string dtBaixa6,
            decimal vlrBaixado,
            decimal vlrJuros,
            decimal vlrDesconto,
            decimal vlrMulta,
            string codContaCaixa,
            string favorecidoNome,
            string idFormaPgto,
            string formaPgtoNome,
            string idLan)
        {
            return BuildBaixaLine(codFilial, codCliFor, codTipoDoc, numDoc, dtBaixa6, vlrBaixado, vlrJuros, vlrDesconto, vlrMulta, codContaCaixa, favorecidoNome, idFormaPgto, formaPgtoNome, idLan, "");
        }

        public static string BuildBaixaLine(
            string codFilial,
            string codCliFor,
            string codTipoDoc,
            string numDoc,
            string dtBaixa6,
            decimal vlrBaixado,
            decimal vlrJuros,
            decimal vlrDesconto,
            decimal vlrMulta,
            string codContaCaixa,
            string favorecidoNome,
            string idFormaPgto,
            string formaPgtoNome)
        {
            return BuildBaixaLine(codFilial, codCliFor, codTipoDoc, numDoc, dtBaixa6, vlrBaixado, vlrJuros, vlrDesconto, vlrMulta, codContaCaixa, favorecidoNome, idFormaPgto, formaPgtoNome, "");
        }
    }
}
