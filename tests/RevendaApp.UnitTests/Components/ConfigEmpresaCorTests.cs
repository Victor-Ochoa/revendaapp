using Bunit;

using Mediator;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using MudBlazor.Utilities;

using RevendaApp.Web.Features.ConfiguracaoEmpresa;

namespace RevendaApp.UnitTests.Components;

/// <summary>
/// A aba "Minha Empresa" trocou o campo hex digitado à mão por um <see cref="MudColorPicker"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que isto precisa de teste:</b> o validador do comando aceita <b>somente</b>
/// <c>^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$</c>. O picker trabalha com
/// <see cref="MudColor"/>, que carrega alfa (8 dígitos). Se a conversão entregasse o hex com alfa,
/// <b>todo salvamento com cor escolhida falharia na validação</b> — e o usuário veria um erro de
/// formato para uma cor que ele acabou de escolher na tela.
/// </para>
/// <para>
/// Os três casos abaixo cobrem a conversão nos dois sentidos e a fiação do componente: um teste que
/// só verificasse "o picker apareceu no markup" passaria mesmo com a conversão quebrada.
/// </para>
/// </remarks>
public class ConfigEmpresaCorTests
{
    /// <summary>
    /// (a) Picker → string: o hex produzido tem de passar no <b>validador real</b> (não um mock),
    /// inclusive quando a cor de origem carrega alfa.
    /// </summary>
    [Theory]
    [InlineData("#0A1F44")]   // 6 dígitos
    [InlineData("#abc")]      // 3 dígitos
    [InlineData("#0A1F44FF")] // com alfa — o caso que quebrava
    public void Cor_escolhida_no_picker_passa_pelo_validador_real(string entrada)
    {
        var hex = CorPrimaria.ParaHex(new MudColor(entrada));

        Assert.Matches("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", hex);

        var resultado = new EditarConfigEmpresaCommandValidator()
            .Validate(new EditarConfigEmpresaCommand("Empresa Teste", CorPrimaria: hex));

        Assert.True(resultado.IsValid, string.Join(" ", resultado.Errors.Select(e => e.ErrorMessage)));
    }

    /// <summary>
    /// (b) String → picker: o valor vem do banco e pode estar nulo, vazio ou legado. A conversão
    /// <b>nunca</b> lança — um throw aqui derrubaria o circuito Blazor ao abrir a aba.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao-e-cor")]
    public void Cor_invalida_no_banco_vira_cor_padrao_sem_excecao(string? entrada)
    {
        var cor = CorPrimaria.ParaPicker(entrada);

        Assert.NotNull(cor);
        Assert.Equal(CorPrimaria.Padrao, cor.ToString(MudColorOutputFormats.Hex), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Um valor já válido <b>não</b> pode ser reescrito: <c>ParaHex</c> devolve minúsculas e a
    /// comparação da auditoria é ordinal (<see cref="StringComparison.Ordinal"/>), então
    /// `#0A1F44` → `#0a1f44` viraria uma linha falsa "Cor primária" em todo salvamento — e um
    /// salvamento que só mexeu em telefone. Ruído na trilha esconde os eventos que importam.
    /// </summary>
    [Theory]
    [InlineData("#0A1F44")]  // 6 dígitos, caixa alta (como está gravado hoje)
    [InlineData("#abc")]     // 3 dígitos
    [InlineData("#FFFFFF")]  // 6 dígitos, tudo maiúsculo
    [InlineData("#0a1f44")]  // 6 dígitos, minúsculo (também é válido)
    public void Cor_valida_no_banco_e_reconhecida_como_hex(string entrada)
    {
        Assert.True(CorPrimaria.EhHexValido(entrada));
    }

    /// <summary>
    /// O complemento: valores nulos, vazios, com espaços ou lixo não são hex válido e caem no
    /// fallback da cor padrão (o picker e o valor enviado precisam concordar).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao-e-cor")]
    [InlineData("#12345")]    // 5 dígitos
    [InlineData("#GGGGGG")]   // dígitos inválidos
    [InlineData("#0A1F44FF")] // com alfa: o validador não aceita 8 dígitos
    public void Cor_invalida_no_banco_nao_e_reconhecida_como_hex(string? entrada)
    {
        Assert.False(CorPrimaria.EhHexValido(entrada));
    }

    /// <summary>
    /// (c) Fiação de ponta a ponta: uma cor escolhida no picker chega ao
    /// <see cref="EditarConfigEmpresaCommand"/> no formato que o validador aceita.
    /// </summary>
    /// <remarks>
    /// A cor disparada no picker (<c>#FF0000FF</c>) é <b>diferente</b> da cor carregada
    /// (<c>#0A1F44</c>) e carrega alfa de propósito: sem isso o comando já sairia válido só por ter
    /// carregado o valor do banco e a asserção passaria por vacuidade.
    /// </remarks>
    [Fact]
    public async Task Componente_envia_ao_comando_a_cor_escolhida_no_picker_em_hex_valido()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AntiforgeryStateProvider, AntiforgerySemToken>();
        var sender = new SenderQueCapturaConfigEmpresa();
        ctx.Services.AddSingleton<ISender>(sender);

        var cut = ctx.Render<ConfigEmpresaTab>();

        cut.WaitForAssertion(() => Assert.Contains("Salvar", cut.Markup, StringComparison.Ordinal));

        var picker = cut.FindComponent<MudColorPicker>();

        // Alfa não existe no domínio (`Empresa.CorPrimaria` é #RGB/#RRGGBB): o picker não deve
        // oferecer transparência que seria descartada em silêncio. `GetState` é o acesso de leitura
        // sancionado pelo analisador do MudBlazor (MUD0012) para parâmetros do componente.
        Assert.False(picker.Instance.GetState(x => x.ShowAlpha));

        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(new MudColor("#FF0000FF")));

        cut.Find("button.mud-button-filled").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sender.ComandoCapturado));

        var comando = sender.ComandoCapturado!;
        Assert.NotNull(comando.CorPrimaria);
        Assert.Matches("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", comando.CorPrimaria);
        Assert.Equal("#ff0000", comando.CorPrimaria, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Salvar sem mexer na cor tem de reenviar o valor gravado <b>exatamente</b> como está: a
    /// auditoria compara com <see cref="StringComparison.Ordinal"/>, então normalizar `#0A1F44`
    /// para `#0a1f44` gravaria uma alteração que não houve.
    /// </summary>
    [Fact]
    public async Task Salvar_sem_editar_a_cor_preserva_o_valor_gravado_verbatim()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AntiforgeryStateProvider, AntiforgerySemToken>();
        var sender = new SenderQueCapturaConfigEmpresa();
        ctx.Services.AddSingleton<ISender>(sender);

        var cut = ctx.Render<ConfigEmpresaTab>();

        cut.WaitForAssertion(() => Assert.Contains("Salvar", cut.Markup, StringComparison.Ordinal));

        cut.Find("button.mud-button-filled").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sender.ComandoCapturado));

        // `#0A1F44` (caixa alta) é o que está no banco: o comando tem de devolver o mesmo texto.
        Assert.Equal("#0A1F44", sender.ComandoCapturado!.CorPrimaria, StringComparer.Ordinal);
    }

    /// <summary>
    /// Quando o banco tem uma cor inválida (ou nenhuma), o picker exibe a cor padrão — e é a cor
    /// padrão que Salvar tem de enviar. Mandar o valor inválido faria o admin ver `#0A1F44` na tela
    /// e receber um erro de formato ao salvar.
    /// </summary>
    /// <remarks>
    /// Prova <b>comportamental</b> do fallback: remover o ramo
    /// <c>EhHexValido ? ... : ParaHex(corCarregada)</c> de <c>CarregarAsync</c> (passando a enviar
    /// sempre `_detalhe.CorPrimaria`) mantém todos os outros testes verdes, porque eles usam uma cor
    /// gravada válida. Este caso é o único que exercita o ramo.
    /// </remarks>
    [Theory]
    [InlineData("nao-e-cor")]
    [InlineData(null)]
    public async Task Cor_invalida_ou_ausente_no_banco_vira_a_padrao_no_comando(string? corGravada)
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AntiforgeryStateProvider, AntiforgerySemToken>();
        var sender = new SenderQueCapturaConfigEmpresa(corGravada!);
        ctx.Services.AddSingleton<ISender>(sender);

        var cut = ctx.Render<ConfigEmpresaTab>();

        cut.WaitForAssertion(() => Assert.Contains("Salvar", cut.Markup, StringComparison.Ordinal));

        // Salvar sem tocar no picker: o valor enviado vem do carregamento.
        cut.Find("button.mud-button-filled").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sender.ComandoCapturado));

        var corEnviada = sender.ComandoCapturado!.CorPrimaria;

        // É a cor padrão (casing indiferente: `ParaHex` devolve minúsculas) e não o valor inválido
        // que estava no banco.
        Assert.Equal(CorPrimaria.Padrao, corEnviada, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(corGravada, corEnviada);
    }

    /// <summary>
    /// Contrato entre <see cref="CorPrimaria.EhHexValido"/> e a regex do validador real para todo
    /// valor <b>preenchido</b>: os dois têm de classificar igual.
    /// </summary>
    /// <remarks>
    /// A regex está <b>duplicada de propósito</b>: a do validador é privada e esta task não pode
    /// editá-la. Este teste é o contrato que mantém as duas iguais — se uma mudar e a outra não, ele
    /// falha e aponta a causa (que seria o retorno do ruído de auditoria, num sentido ou no outro).
    /// </remarks>
    [Theory]
    [InlineData("#abc")]
    [InlineData("#ABC")]
    [InlineData("#0A1F44")]
    [InlineData("#0a1f44")]
    [InlineData("#12345")]      // 5 dígitos — inválido
    [InlineData("#GGGGGG")]     // não-hex
    [InlineData("#0A1F44FF")]   // com alfa — inválido para o validador
    [InlineData("nao-e-cor")]
    public void EhHexValido_concorda_com_o_validador_real(string entrada)
    {
        var aceitoPeloValidador = new EditarConfigEmpresaCommandValidator()
            .Validate(new EditarConfigEmpresaCommand("Empresa Teste", CorPrimaria: entrada)).IsValid;

        Assert.Equal(aceitoPeloValidador, CorPrimaria.EhHexValido(entrada));
    }

    /// <summary>
    /// Vazio/nulo é a <b>única</b> divergência deliberada entre os dois: o validador trata a cor
    /// como campo opcional (o `.When(!IsNullOrWhiteSpace)` dispensa a regra) e aceita o vazio;
    /// <see cref="CorPrimaria.EhHexValido"/> responde à pergunta mais estreita "isto é um hex?" e
    /// devolve <c>false</c>.
    /// </summary>
    /// <remarks>
    /// A divergência é necessária: se <c>EhHexValido(null)</c> fosse <c>true</c>, a aba manteria o
    /// vazio no comando enquanto o picker exibe a cor padrão — o admin veria uma cor que não é a
    /// gravada. Por isso o componente substitui o vazio pela padrão, e este teste trava esse
    /// contrato em vez de deixá-lo implícito.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cor_vazia_e_aceita_pelo_validador_mas_nao_e_hex(string? entrada)
    {
        var aceitoPeloValidador = new EditarConfigEmpresaCommandValidator()
            .Validate(new EditarConfigEmpresaCommand("Empresa Teste", CorPrimaria: entrada)).IsValid;

        Assert.True(aceitoPeloValidador);
        Assert.False(CorPrimaria.EhHexValido(entrada));
    }

    /// <summary>
    /// Limpar a cor no picker (<c>ClearAsync</c>, alcançável pela UI pelo switch de modo) entrega
    /// <b>null</b> ao <c>ValueChanged</c>. Um handler que dereferencia o valor nulo derruba o
    /// circuito Blazor e o admin perde o formulário inteiro.
    /// </summary>
    /// <remarks>
    /// O teste falha antes do guard (a exceção sobe pelo callback) e passa depois: o componente
    /// continua renderizado e o Salvar seguinte ainda envia um hex válido — a limpeza do picker
    /// não contamina o valor do domínio.
    /// </remarks>
    [Fact]
    public async Task Limpar_a_cor_no_picker_nao_derruba_o_circuito()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<AntiforgeryStateProvider, AntiforgerySemToken>();
        var sender = new SenderQueCapturaConfigEmpresa();
        ctx.Services.AddSingleton<ISender>(sender);

        var cut = ctx.Render<ConfigEmpresaTab>();

        cut.WaitForAssertion(() => Assert.Contains("Salvar", cut.Markup, StringComparison.Ordinal));

        var picker = cut.FindComponent<MudColorPicker>();

        // Não deve lançar: o guard transforma o null do picker em no-op.
        await cut.InvokeAsync(() => picker.Instance.ClearAsync());

        // O componente sobreviveu à limpeza (o circuito não foi derrubado) e o formulário continua
        // utilizável. Salvar prova que o valor do domínio não foi zerado junto com a exibição.
        Assert.Contains("Salvar", cut.Markup, StringComparison.Ordinal);
        cut.Find("button.mud-button-filled").Click();

        cut.WaitForAssertion(() => Assert.NotNull(sender.ComandoCapturado));
        Assert.Matches("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", sender.ComandoCapturado!.CorPrimaria!);
    }

    /// <summary>
    /// <see cref="AntiforgeryToken"/> injeta <see cref="AntiforgeryStateProvider"/>; sem provedor o
    /// componente lançaria ao renderizar. Devolver <c>null</c> é o caso "sem token", que o
    /// componente trata renderizando nada — o form nativo não é o objeto deste teste.
    /// </summary>
    private sealed class AntiforgerySemToken : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    /// <summary>
    /// <see cref="ISender"/> de teste: responde o detalhe da empresa e captura o comando de edição
    /// para que o teste asserte sobre o que a tela <b>realmente enviou</b>.
    /// </summary>
    /// <param name="corGravada">
    /// Cor que a "empresa" tem no banco. O default é o valor típico; os testes de fallback passam um
    /// valor inválido ou nulo.
    /// </param>
    private sealed class SenderQueCapturaConfigEmpresa(string corGravada = "#0A1F44") : ISender
    {
        public EditarConfigEmpresaCommand? ComandoCapturado { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A aba usa as sobrecargas IQuery e ICommand.");

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            if (command is EditarConfigEmpresaCommand editar)
            {
                ComandoCapturado = editar;
                return ValueTask.FromResult((TResponse)(object)true);
            }

            throw new NotSupportedException($"Comando não esperado no teste: {command.GetType().Name}.");
        }

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            if (query is ObterConfigEmpresaQuery)
            {
                return ValueTask.FromResult((TResponse)(object)new ConfigEmpresaDetalhe(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "empresa-teste",
                    "Empresa Teste",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [],
                    corGravada,
                    null));
            }

            throw new NotSupportedException($"Consulta não esperada no teste: {query.GetType().Name}.");
        }

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A aba só usa o Send tipado.");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}