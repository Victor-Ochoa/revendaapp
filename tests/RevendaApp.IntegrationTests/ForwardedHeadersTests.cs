namespace RevendaApp.IntegrationTests.Tests;

/// <summary>
/// Integração ponta a ponta do processamento de <c>X-Forwarded-Proto</c> pelo middleware
/// <c>UseForwardedHeaders</c> (o teste unitário de <c>ForwardedHeadersConfig</c> só valida o objeto
/// de opções; não prova que o middleware está no pipeline).
/// </summary>
/// <remarks>
/// <para>
/// <b>O que este teste prova:</b> o <c>UseForwardedHeaders</c> está registrado <b>antes</b> do
/// <c>UseHttpsRedirection</c> e aplica o proto encaminhado de um proxy <b>confiável</b>. Pedir a
/// raiz por HTTPS com <c>X-Forwarded-Proto: http</c> faz o app acreditar que a requisição chegou por
/// HTTP, e o <c>UseHttpsRedirection</c> responde com um 307 para a URL HTTPS; sem o middleware o
/// esquema continuaria <c>https</c> e a resposta seria o redirect de autenticação (302 para
/// <c>/login</c>). O caso sem o header é o controle positivo de que a diferença vem do header.
/// </para>
/// <para>
/// <b>O que este teste NÃO prova:</b> a rejeição de <c>X-Forwarded-Proto</c> vindo de uma origem
/// <b>não confiável</b> (Review Focus #2 do plano). Não é observável por <see cref="WebAppFixture"/>:
/// em modo dev o app escuta só em loopback e <c>ForwardedHeadersConfig</c> adiciona
/// <see cref="System.Net.IPAddress.Loopback"/> a <c>KnownProxies</c> incondicionalmente, então toda
/// requisição do <c>HttpClient</c> da fixture chega de um endereço confiável — um assert de "header
/// forjado ignorado" passaria mesmo com a configuração errada (teste vacuoso). Essa garantia é
/// pinada onde ela é real: <c>ForwardedHeadersConfigTests</c> (a lista de redes confiáveis e a
/// recusa de CIDR malformado).
/// </para>
/// </remarks>
public class ForwardedHeadersTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    [Fact]
    public async Task ProtoEncaminhadoDeProxyConfiavel_EhProcessadoAntesDoHttpsRedirection()
    {
        using var client = fixture.CreateClient();

        // Controle: sem o header, o esquema real é https e o app segue para a autenticação.
        using var semHeader = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, semHeader.StatusCode);
        Assert.Contains("/login", semHeader.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // Com o header de um proxy confiável, o app vê http e o UseHttpsRedirection redireciona.
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/");
        requisicao.Headers.Add("X-Forwarded-Proto", "http");
        using var comHeader = await client.SendAsync(requisicao);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, comHeader.StatusCode);
        Assert.Equal(
            Uri.UriSchemeHttps,
            comHeader.Headers.Location!.Scheme,
            StringComparer.OrdinalIgnoreCase);
    }
}