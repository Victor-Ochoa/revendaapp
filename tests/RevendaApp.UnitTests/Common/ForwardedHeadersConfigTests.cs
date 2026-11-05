using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

using RevendaApp.Web.Common;

namespace RevendaApp.UnitTests.Common;

/// <summary>
/// Em produção o Caddy roda no host e faz <c>reverse_proxy</c> para a porta publicada do container.
/// O Kestrel então enxerga o gateway da bridge do Docker (172.x), não o loopback — confiar só em
/// loopback deixaria o recurso inútil em produção. Por outro lado, aceitar qualquer origem permitiria
/// que um cliente externo forjasse <c>X-Forwarded-Proto: https</c> e recebesse cookie <c>Secure</c>
/// sobre HTTP (sessão descartada pelo navegador).
/// </summary>
public class ForwardedHeadersConfigTests
{
    [Fact]
    public void Configurar_processa_apenas_proto_e_host_do_proxy_local()
    {
        var opcoes = new ForwardedHeadersOptions();

        ForwardedHeadersConfig.Configurar(opcoes);

        Assert.Equal(ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, opcoes.ForwardedHeaders);
        Assert.Equal(1, opcoes.ForwardLimit);
        Assert.Contains(IPAddress.Loopback, opcoes.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, opcoes.KnownProxies);
        Assert.Contains(opcoes.KnownIPNetworks, rede => rede.Contains(IPAddress.Loopback));
    }

    [Fact]
    public void Configurar_confia_nas_redes_privadas_do_proxy_reverso()
    {
        var opcoes = new ForwardedHeadersOptions();

        ForwardedHeadersConfig.Configurar(opcoes);

        // Gateway da bridge do Docker (172.16.0.0/12) é o salto real Caddy → container.
        Assert.True(EhConfiavel(opcoes, IPAddress.Parse("172.17.0.1")));
        Assert.True(EhConfiavel(opcoes, IPAddress.Parse("10.0.0.5")));
        Assert.True(EhConfiavel(opcoes, IPAddress.Parse("192.168.15.2")));
    }

    [Fact]
    public void Configurar_nao_confia_em_qualquer_endereco()
    {
        var opcoes = new ForwardedHeadersOptions();

        ForwardedHeadersConfig.Configurar(opcoes);

        Assert.DoesNotContain(opcoes.KnownIPNetworks, rede => rede.PrefixLength == 0);
        Assert.DoesNotContain(opcoes.KnownProxies, ip => ip.ToString() == "0.0.0.0");
    }

    [Fact]
    public void Configurar_nao_confia_em_ip_publico_que_forja_x_forwarded_proto()
    {
        var opcoes = new ForwardedHeadersOptions();

        ForwardedHeadersConfig.Configurar(opcoes);

        Assert.False(EhConfiavel(opcoes, IPAddress.Parse("203.0.113.10")));
        Assert.False(EhConfiavel(opcoes, IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Configurar_respeita_redes_confiaveis_vindas_da_configuracao()
    {
        var opcoes = new ForwardedHeadersOptions();

        ForwardedHeadersConfig.Configurar(opcoes, "127.0.0.0/8, 10.1.0.0/16");

        Assert.True(EhConfiavel(opcoes, IPAddress.Parse("10.1.2.3")));
        // A lista configurada SUBSTITUI os defaults: o resto do 10/8 deixa de ser confiável.
        Assert.False(EhConfiavel(opcoes, IPAddress.Parse("10.2.0.1")));
        Assert.False(EhConfiavel(opcoes, IPAddress.Parse("192.168.1.5")));
    }

    [Theory]
    [InlineData("010.0.0.0/8")] // octal → 8.0.0.0/8, faixa PÚBLICA
    [InlineData("10/8")]        // abreviado → 0.0.0.10/8, amplia a confiança
    [InlineData("0.0.0.0/0")]   // confiança universal
    [InlineData("172.17.0.1/8")] // host bits → 172.0.0.0/8, faixa PÚBLICA
    [InlineData("10.1.2.3/8")]   // host bits → 10.0.0.0/8, confiança 256x maior
    public void Configurar_recusa_cidr_malformado_em_vez_de_ampliar_a_confianca(string cidr)
    {
        var opcoes = new ForwardedHeadersOptions();

        // IPAddress.TryParse é leniente: "10" vira 0.0.0.10 e "010.0.0.0" é lido como octal
        // (8.0.0.0, faixa PÚBLICA). Aceitar isso transformaria um erro de digitação em confiança
        // silenciosamente maior — o contrato da classe é falhar alto. O mesmo vale para bits de
        // host: System.Net.IPNetwork trunca "172.17.0.1/8" para 172.0.0.0/8 sem avisar, e o
        // operador que copia o IP do proxy do deploy/README.md erra a máscara com facilidade.
        Assert.Throws<InvalidOperationException>(
            () => ForwardedHeadersConfig.Configurar(opcoes, cidr));
    }

    /// <summary>
    /// Controle positivo do caso acima: o CIDR na forma canônica (endereço já é o endereço de
    /// rede) continua aceito — a rejeição de bits de host não pode quebrar a configuração normal.
    /// </summary>
    [Theory]
    [InlineData("172.16.0.0/12")]
    [InlineData("10.1.0.0/16")]
    [InlineData("192.168.0.0/16")]
    public void Configurar_aceita_cidr_na_forma_canonica(string cidr)
    {
        var opcoes = new ForwardedHeadersOptions();

        ForwardedHeadersConfig.Configurar(opcoes, cidr);

        Assert.Contains(opcoes.KnownIPNetworks, rede => rede.ToString() == cidr);
    }

    private static bool EhConfiavel(ForwardedHeadersOptions opcoes, IPAddress ip) =>
        opcoes.KnownProxies.Contains(ip)
        || opcoes.KnownIPNetworks.Any(rede => rede.Contains(ip));
}