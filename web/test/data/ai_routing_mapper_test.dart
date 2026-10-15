import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/settings_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';

void main() {
  test('the new enums mirror the C# declaration order, since they cross the wire as ordinals', () {
    // A member inserted on either side silently re-maps every value; pin the positions.
    expect(AiProvider.routed.index, 5);
    expect(AiWireProtocol.values.map((p) => p.name),
        ['openAiChatCompletions', 'openAiResponses', 'anthropic', 'google', 'ollama']);
    expect(AiAuthMode.values.map((a) => a.name), ['apiKey', 'none', 'googleCloud']);
    expect(AiFeature.values.map((f) => f.name), ['scriptResearch', 'scriptRepair', 'cpeSuggestion']);
    expect(WebSearchBackend.values.map((b) => b.name), ['none', 'ollamaWeb', 'tavily', 'brave', 'searXng']);
  });

  test('routing reads connections and routes, and follows the server\'s fallback chain', () {
    final routing = aiRoutingFromJson({
      'connections': [
        {
          'id': 'c1',
          'name': 'Vertex',
          'catalogProviderId': 'google-vertex',
          'protocol': 3,
          'authMode': 2,
          'hasApiKey': false,
          'googleCloudProject': 'corp-tools',
          'googleCloudLocation': 'global',
          'useHostedWebSearch': true,
        },
      ],
      'routes': [
        {'feature': 0, 'connectionId': 'c1', 'model': 'gemini-2.5-pro'},
        {'feature': 1, 'connectionId': 'c1', 'model': 'gemini-2.5-flash'},
      ],
    });

    final vertex = routing.connections.single;
    expect(vertex.protocol, AiWireProtocol.google);
    expect(vertex.authMode, AiAuthMode.googleCloud);
    expect(vertex.endpointLabel, 'Vertex AI · corp-tools · global');
    // CPE suggestion has no route of its own: it takes repair's, as AiProviderSettings.RouteFor does.
    expect(routing.routeFor(AiFeature.cpeSuggestion), isNull);
    expect(routing.effectiveRouteFor(AiFeature.cpeSuggestion)!.model, 'gemini-2.5-flash');
  });

  test('the catalog keeps a model\'s own protocol', () {
    final catalog = aiCatalogFromJson({
      'fetchedAtUtc': '2026-10-07T00:00:00+00:00',
      'isStale': false,
      'providers': [
        {
          'id': 'google-vertex',
          'name': 'Vertex',
          'protocol': 3,
          'usesGoogleCloud': true,
          'requiresApiKey': false,
          'models': [
            {'id': 'gemini-2.5-pro', 'name': 'Gemini 2.5 Pro', 'protocol': 3, 'toolCall': true},
            {'id': 'claude-sonnet-4-5@20250929', 'name': 'Claude Sonnet 4.5', 'protocol': 2, 'toolCall': true},
          ],
        },
      ],
    });

    final models = catalog.provider('google-vertex')!.models;
    expect(models.map((m) => m.protocol), [AiWireProtocol.google, AiWireProtocol.anthropic]);
  });

  test('an older server, without the web-search fields, reads as none configured', () {
    final settings = aiAgentSettingsFromJson({'provider': 0, 'isEnabled': true, 'hasApiKey': true});
    expect(settings.webSearchBackend, WebSearchBackend.none);
    expect(settings.hasWebSearchApiKey, isFalse);
  });
}
