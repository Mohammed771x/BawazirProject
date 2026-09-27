import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/http_wordos_api.dart';
import 'package:wordos/core/api/wordos_api.dart';

/// Staying signed in (ADR-093).
///
/// Learners on Android were being asked to sign in again "after a while", with
/// nothing to explain it. The cause was here: an expired access token is
/// renewed with the refresh token, and **any** failure of that exchange was
/// read as a dead session — so a dropped connection, a timeout, or a server
/// still waking up deleted their credentials.
///
/// These run a real HTTP server on loopback and script its answers, because
/// what is being tested is the interceptor's behaviour against a socket, and a
/// stubbed client would let the very distinction under test be assumed.
void main() {
  late HttpServer server;
  late List<String> paths;
  late int signedOut;
  String? token;
  String? refreshToken;

  /// What the scripted server answers `/auth/refresh` with.
  late int refreshStatus;
  late Object? refreshBody;

  /// Whether `/hub` accepts the token it is given.
  late bool hubAcceptsToken;

  Future<HttpWordOsApi> start() async {
    paths = [];
    signedOut = 0;
    token = 'expired-access-token';
    refreshToken = 'a-good-refresh-token';

    server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);

    server.listen((request) async {
      paths.add(request.uri.path);

      if (request.uri.path.endsWith('/auth/refresh')) {
        request.response.statusCode = refreshStatus;
        if (refreshBody != null) {
          request.response.headers.contentType = ContentType.json;
          request.response.write(jsonEncode(refreshBody));
        }
        await request.response.close();
        return;
      }

      final authorized = hubAcceptsToken &&
          request.headers.value('authorization') == 'Bearer new-access-token';

      request.response.statusCode = authorized ? 200 : 401;
      if (authorized) {
        request.response.headers.contentType = ContentType.json;
        request.response.write(jsonEncode({
          'dailyProgress': {'wordsAddedToday': 0, 'dailyTarget': 10},
          'skills': [],
          'weeklyReview': {'available': false, 'wordCount': 0},
          'vocabulary': {'learning': 0, 'active': 0, 'archived': 0},
        }));
      }
      await request.response.close();
    });

    return HttpWordOsApi(
      baseUrl: 'http://127.0.0.1:${server.port}/api',
      tokenReader: () => token,
      refreshTokenReader: () => refreshToken,
      onRefreshed: (t, r) {
        token = t;
        if (r != null) refreshToken = r;
      },
      onUnauthorized: () {
        signedOut++;
        token = null;
        refreshToken = null;
      },
    );
  }

  setUp(() {
    refreshStatus = 200;
    refreshBody = {'token': 'new-access-token', 'refreshToken': 'rotated'};
    hubAcceptsToken = true;
  });

  tearDown(() async => server.close(force: true));

  test('an expired token is renewed and the request replayed', () async {
    // The ordinary case, and the one that must keep working: a learner
    // mid-session never notices their access token expiring.
    final api = await start();

    await api.hub();

    expect(signedOut, 0);
    expect(token, 'new-access-token');
    expect(refreshToken, 'rotated');
    expect(paths.where((p) => p.endsWith('/hub')).length, 2,
        reason: 'the original request was replayed with the new token');
  });

  test('a refused refresh token really does end the session', () async {
    // The other half. If this stopped signing people out, a revoked session
    // would live for ever on the device.
    refreshStatus = 401;
    refreshBody = null;

    final api = await start();

    await expectLater(api.hub(), throwsA(isA<ApiException>()));

    expect(signedOut, 1);
    expect(token, isNull);
  });

  test('a server that is down does not sign the learner out', () async {
    // The bug. 503 is a server waking up, a deploy, a proxy hiccup — nobody
    // has said this session is over, and deleting the learner's credentials
    // because of it is what they experienced as "it logged me out for no
    // reason".
    refreshStatus = 503;
    refreshBody = null;

    final api = await start();

    await expectLater(api.hub(), throwsA(isA<ApiException>()));

    expect(signedOut, 0, reason: 'a 503 is not a rejection');
    expect(token, isNotNull, reason: 'the credentials survive');
    expect(refreshToken, 'a-good-refresh-token');
  });

  test('an unreachable server does not sign the learner out', () async {
    // A train going into a tunnel. There is no response at all here — the
    // socket refuses — which is the most common way this happens on a phone.
    final api = await start();
    final port = server.port;
    await server.close(force: true);
    server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);

    expect(port, isNot(server.port));

    await expectLater(api.hub(), throwsA(isA<ApiException>()));

    expect(signedOut, 0);
    expect(token, isNotNull);
    expect(refreshToken, isNotNull);
  });

  test('once the connection is back, the same session recovers', () async {
    // The consequence of not signing them out: nothing had to be re-entered.
    // The access token is still expired, the next request 401s, the refresh
    // is tried again — and this time it works.
    refreshStatus = 503;
    refreshBody = null;

    final api = await start();
    await expectLater(api.hub(), throwsA(isA<ApiException>()));

    refreshStatus = 200;
    refreshBody = {'token': 'new-access-token', 'refreshToken': 'rotated'};

    await api.hub();

    expect(signedOut, 0);
    expect(token, 'new-access-token');
  });

  test('a session with nothing to exchange is over', () async {
    // No refresh token at all. Not a network problem: there is genuinely no
    // session to recover, and pretending otherwise strands the learner on a
    // screen where nothing works.
    final api = await start();
    refreshToken = null;

    await expectLater(api.hub(), throwsA(isA<ApiException>()));

    expect(signedOut, 1);
  });
}
