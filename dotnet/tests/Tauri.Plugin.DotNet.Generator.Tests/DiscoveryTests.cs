using Tauri.Plugin.DotNet.Generator;
using Tauri.Plugin.DotNet.Generator.Tests.Helpers;
using Tauri.Plugin.DotNet.Generator.Tests.Fixtures;

namespace Tauri.Plugin.DotNet.Generator.Tests;

public class DiscoveryTests : IClassFixture<AssemblyHelper>
{
    private readonly AssemblyHelper _helper;

    public DiscoveryTests(AssemblyHelper helper)
    {
        _helper = helper;
    }

    #region DiscoverServices

    [Fact]
    public void DiscoverServices_FindsServicesWithAttribute()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);

        Assert.Contains(services, s => s.Name == "BasicService");
        Assert.Contains(services, s => s.Name == "AsyncService");
        Assert.Contains(services, s => s.Name == "ModelService");
    }

    [Fact]
    public void DiscoverServices_SkipsClassWithoutAttribute()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);

        Assert.DoesNotContain(services, s => s.Name == "NotAService");
    }

    [Fact]
    public void DiscoverServices_UsesCustomNameFromAttribute()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);

        // CustomNamedService has [BridgeService(Name = "CustomApi")]
        Assert.Contains(services, s => s.Name == "CustomApi");
        Assert.DoesNotContain(services, s => s.Name == "CustomNamedService");
    }

    [Fact]
    public void DiscoverServices_ExcludesIgnoredMethods()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "IgnoredMethodService");

        Assert.Contains(svc.Methods, m => m.Name == "Visible");
        Assert.DoesNotContain(svc.Methods, m => m.Name == "Hidden");
    }

    [Fact]
    public void DiscoverServices_FiltersCancellationTokenParameters()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "CancellationService");

        var slowMethod = svc.Methods.First(m => m.Name == "SlowMethod");
        // Should have only 'seconds' param, CancellationToken stripped
        Assert.Single(slowMethod.Parameters);
        Assert.Equal("seconds", slowMethod.Parameters[0].Name);
    }

    [Fact]
    public void DiscoverServices_FiltersCancellationToken_KeepsOtherParams()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "CancellationService");

        var multiParam = svc.Methods.First(m => m.Name == "MultiParam");
        // Should have 'name' and 'count', CancellationToken stripped
        Assert.Equal(2, multiParam.Parameters.Count);
        Assert.Equal("name", multiParam.Parameters[0].Name);
        Assert.Equal("count", multiParam.Parameters[1].Name);
    }

    [Fact]
    public void DiscoverServices_FiltersCallContextParameters()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "CallContextService");

        var getTitle = svc.Methods.First(m => m.Name == "GetTitle");
        Assert.Empty(getTitle.Parameters);

        var getTitleWithArg = svc.Methods.First(m => m.Name == "GetTitleWithArg");
        Assert.Single(getTitleWithArg.Parameters);
        Assert.Equal("name", getTitleWithArg.Parameters[0].Name);
    }

    [Fact]
    public void DiscoverServices_SkipsSpecialMethods()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "BasicService");

        // Should not contain property getters/setters or compiler-generated methods
        Assert.All(svc.Methods, m => Assert.False(m.Name.StartsWith("get_") || m.Name.StartsWith("set_")));
    }

    [Fact]
    public void DiscoverServices_DetectsAsyncMethods()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "AsyncService");

        var getAsync = svc.Methods.First(m => m.Name == "GetAsync");
        Assert.True(getAsync.IsAsync);

        var getValueAsync = svc.Methods.First(m => m.Name == "GetValueAsync");
        Assert.True(getValueAsync.IsAsync);
    }

    [Fact]
    public void DiscoverServices_BasicService_HasCorrectMethods()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "BasicService");

        Assert.Equal(3, svc.Methods.Count);
        Assert.Contains(svc.Methods, m => m.Name == "Greet");
        Assert.Contains(svc.Methods, m => m.Name == "Add");
        Assert.Contains(svc.Methods, m => m.Name == "DoNothing");
    }

    [Fact]
    public void DiscoverServices_EmptyService_HasNoMethods()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "EmptyService");

        Assert.Empty(svc.Methods);
    }

    [Fact]
    public void DiscoverServices_AllIgnoredService_HasNoMethods()
    {
        var services = ServiceDiscovery.DiscoverServices(_helper.TestAssembly);
        var svc = services.First(s => s.Name == "AllIgnoredService");

        Assert.Empty(svc.Methods);
    }

    #endregion

    #region DiscoverEvents

    [Fact]
    public void DiscoverEvents_FindsEventsWithAttribute()
    {
        var events = ServiceDiscovery.DiscoverEvents(_helper.TestAssembly);

        Assert.Contains(events, e => e.Name == "test_event");
        Assert.Contains(events, e => e.Name == "another");
    }

    [Fact]
    public void DiscoverEvents_SkipsClassWithoutAttribute()
    {
        var events = ServiceDiscovery.DiscoverEvents(_helper.TestAssembly);

        // NotAnEvent should not appear
        Assert.DoesNotContain(events, e =>
            e.PayloadType.Name == "NotAnEvent");
    }

    [Fact]
    public void DiscoverEvents_ReadsEventNameFromConstructorArg()
    {
        var events = ServiceDiscovery.DiscoverEvents(_helper.TestAssembly);

        var testEvent = events.First(e => e.Name == "test_event");
        Assert.Equal("TestEvent", testEvent.PayloadType.Name);

        var another = events.First(e => e.Name == "another");
        Assert.Equal("AnotherEvent", another.PayloadType.Name);
    }

    #endregion

    #region Methods that share a name

    [Fact]
    public void BuildService_RejectsOverloadsAndNamesThatDifferOnlyInCase()
    {
        var ex = Assert.Throws<BindingException>(
            () => ServiceDiscovery.BuildService(_helper.LoadType(typeof(OverloadedFixture)), "Overloaded"));

        Assert.Equal(Diagnostics.OverloadedMethod, ex.Code);
        Assert.Contains("Service 'Overloaded' (OverloadedFixture)", ex.Message);
        Assert.Contains("Foo (2 methods)", ex.Message);
        Assert.Contains("Baz/baz (2 methods)", ex.Message);
        Assert.Contains("[BridgeIgnore]", ex.Message);
        // Neither a unique name nor a name whose other overload is [BridgeIgnore] is a problem
        Assert.DoesNotContain("Single", ex.Message);
        Assert.DoesNotContain("Hidden", ex.Message);
    }

    [Fact]
    public void BuildService_AllowsAnOverloadHiddenWithBridgeIgnore()
    {
        var service = ServiceDiscovery.BuildService(_helper.LoadType(typeof(IgnoredOverloadFixture)), "Ignored");

        Assert.Equal(new[] { "Hidden", "Other" }, service.Methods.Select(m => m.Name).OrderBy(n => n));
        Assert.Empty(service.Methods.First(m => m.Name == "Hidden").Parameters);
    }

    [Fact]
    public void BuildFrontend_RejectsOverloads_WithoutSuggestingBridgeIgnore()
    {
        var ex = Assert.Throws<BindingException>(
            () => ServiceDiscovery.BuildFrontend(_helper.LoadType(typeof(IOverloadedFrontend)), "Overloaded"));

        Assert.Equal(Diagnostics.OverloadedMethod, ex.Code);
        Assert.Contains("Frontend interface 'IOverloadedFrontend'", ex.Message);
        Assert.Contains("Ask (2 methods)", ex.Message);
        // [BridgeIgnore] is for service methods; an interface method cannot be hidden with it
        Assert.DoesNotContain("[BridgeIgnore]", ex.Message);
    }

    [Fact]
    public void RequireUniqueNames_AcceptsUniqueNames()
        => ServiceDiscovery.RequireUniqueNames("Service 'X'", Methods("A", "B", "C"), canIgnore: true);

    [Fact]
    public void RequireUniqueNames_ListsEveryShare()
    {
        var ex = Assert.Throws<BindingException>(
            () => ServiceDiscovery.RequireUniqueNames("Service 'X'", Methods("A", "a", "B", "C", "C", "C"), canIgnore: true));

        Assert.Contains("A/a (2 methods)", ex.Message);
        Assert.Contains("C (3 methods)", ex.Message);
        Assert.DoesNotContain("B (", ex.Message);
    }

    private static List<MethodDef> Methods(params string[] names) =>
        names.Select(n => new MethodDef(n, new List<ParamDef>(), typeof(string), IsAsync: false)).ToList();

    #endregion
}
