using System.Net;

using Microsoft.AspNetCore.HttpOverrides;

namespace RevendaApp.Web.Common;

/// <summary>
/// Configura o processamento de <c>X-Forwarded-*</c> para o proxy reverso (Caddy no host).
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que loopback + redes privadas:</b> em produção o Caddy roda no host e faz
/// <c>reverse_proxy</c> para a porta <b>publicada</b> do container. Nesse caminho o Kestrel enxerga o
/// gateway da bridge do Docker (172.16.0.0/12), <b>não</b> o loopback — confiar apenas em loopback
/// deixaria o app achando que o esquema é <c>http</c>: o <c>UseHttpsRedirection</c>, as URLs
/// absolutas e redirects gerados sairiam com o esquema errado e o HSTS não seria aplicado. (O
/// cookie <c>Secure</c> não depende disso: fora de Development o <c>SecurePolicy</c> é <c>Always</c>.)
/// Por isso confiamos nas faixas RFC1918 (loopback, 10/8, 172.16/12, 192.168/16), que só existem
/// dentro da infraestrutura do host/Docker. Um cliente externo que enviasse
/// <c>X-Forwarded-Proto: https</c> direto ao Kestrel continua sendo ignorado.
/// </para>
/// <para>
/// <b>Por que não processar <c>X-Forwarded-For</c>:</b> o app não usa o IP do cliente para
/// autorização nem auditoria; processá-lo só aumentaria a superfície de spoofing.
/// </para>
/// <para>
/// <b>Override:</b> a chave de configuração <c>Proxy:RedesConfiaveis</c> (lista de CIDRs separada
/// por vírgula) substitui os defaults — útil quando o proxy está em outra rede.
/// </para>
/// </remarks>
public static class ForwardedHeadersConfig
{
    /// <summary>Chave de configuração com os CIDRs confiáveis, separados por vírgula.</summary>
    public const string ChaveRedesConfiaveis = "Proxy:RedesConfiaveis";

    /// <summary>Faixas confiáveis por padrão: loopback + RFC1918 (host/Docker).</summary>
    private static readonly string[] RedesPadrao =
    [
        "127.0.0.0/8",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
    ];

    /// <summary>
    /// Aplica a política de proxies confiáveis. <paramref name="redesConfiaveis"/> é a lista de CIDRs
    /// separada por vírgula vinda de <see cref="ChaveRedesConfiaveis"/>; quando vazia, usa os
    /// defaults (loopback + RFC1918).
    /// </summary>
    public static void Configurar(ForwardedHeadersOptions opcoes, string? redesConfiaveis = null)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        opcoes.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        // Um único salto (Caddy). Limitar evita que uma cadeia forjada de X-Forwarded-* seja aceita.
        opcoes.ForwardLimit = 1;

        opcoes.KnownProxies.Clear();
        opcoes.KnownProxies.Add(IPAddress.Loopback);
        opcoes.KnownProxies.Add(IPAddress.IPv6Loopback);

        // KnownNetworks está obsoleto no .NET 10 (ASPDEPR005) — usar KnownIPNetworks
        // (DualIPNetworkList de System.Net.IPNetwork).
        opcoes.KnownIPNetworks.Clear();
        foreach (var cidr in Cidrs(redesConfiaveis))
        {
            opcoes.KnownIPNetworks.Add(ParseCidr(cidr));
        }
    }

    private static string[] Cidrs(string? redesConfiaveis)
    {
        if (string.IsNullOrWhiteSpace(redesConfiaveis))
        {
            return RedesPadrao;
        }

        var cidrs = redesConfiaveis
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Configuração só com vírgulas/espaços não pode "zerar" a confiança silenciosamente.
        return cidrs.Length == 0 ? RedesPadrao : cidrs;
    }

    private static System.Net.IPNetwork ParseCidr(string cidr)
    {
        var partes = cidr.Split('/', StringSplitOptions.TrimEntries);

        // `IPAddress.TryParse` é leniente: "10" vira 0.0.0.10 e "010.0.0.0" é lido como octal
        // (8.0.0.0, faixa PÚBLICA). Comparar a forma canônica com o texto informado rejeita os dois
        // casos — sem isso, um erro de digitação ampliaria a confiança em silêncio, que é
        // exatamente o que esta classe promete não fazer.
        if (partes.Length != 2
            || !IPAddress.TryParse(partes[0], out var endereco)
            || endereco.ToString() != partes[0]
            || !int.TryParse(partes[1], out var prefixo)
            // Prefixo 0 = confiança universal (aceitaria X-Forwarded-* de qualquer origem).
            || prefixo < 1
            || prefixo > (endereco.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
        {
            // Falha explícita: um CIDR inválido em produção não pode ser ignorado em silêncio.
            throw new InvalidOperationException(
                $"CIDR inválido em {ChaveRedesConfiaveis}: '{cidr}'. Use o formato endereço/prefixo (ex.: 172.16.0.0/12).");
        }

        var rede = new System.Net.IPNetwork(endereco, prefixo);

        // `System.Net.IPNetwork` TRUNCA os bits de host em silêncio: 172.17.0.1/8 vira 172.0.0.0/8
        // (faixa PÚBLICA) — exatamente o que um operador digita ao copiar o IP do proxy e errar a
        // máscara. Exigir que o endereço seja o da rede mantém a promessa de falhar alto em vez de
        // alargar a confiança.
        if (!rede.BaseAddress.Equals(endereco))
        {
            throw new InvalidOperationException(
                $"CIDR inválido em {ChaveRedesConfiaveis}: '{cidr}' tem bits de host setados; "
                + $"use o endereço de rede '{rede}'.");
        }

        return rede;
    }
}