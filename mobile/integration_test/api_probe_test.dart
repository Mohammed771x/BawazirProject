import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/core/api/http_wordos_api.dart';
import 'package:wordos/core/models/models.dart';

/// Calls the API through the app's own networking layer, with no UI involved.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const email = String.fromEnvironment('WORDOS_TEST_EMAIL');
  const password = String.fromEnvironment('WORDOS_TEST_PASSWORD');
  const baseUrl = String.fromEnvironment('WORDOS_API_BASE_URL');

  testWidgets('startSession(spelling) returns', (tester) async {
    String? token;
    final api = HttpWordOsApi(baseUrl: baseUrl, tokenReader: () => token);

    final auth = await api.login(email: email, password: password);
    token = auth.token;
    debugPrint('LOGIN OK: ${auth.user.email}');

    for (final skill in [SkillType.reading, SkillType.spelling]) {
      final started = DateTime.now();
      try {
        final session = await api
            .startSession(skill)
            .timeout(const Duration(seconds: 25));
        final ms = DateTime.now().difference(started).inMilliseconds;
        debugPrint('${skill.name}: ok in ${ms}ms · items=${session.items.length}');
      } catch (e) {
        final ms = DateTime.now().difference(started).inMilliseconds;
        debugPrint('${skill.name}: FAILED after ${ms}ms · $e');
      }
    }
  });
}
