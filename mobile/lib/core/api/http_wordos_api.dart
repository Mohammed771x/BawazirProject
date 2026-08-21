import 'package:dio/dio.dart';

import '../models/models.dart';
import 'api_providers.dart';
import 'wordos_api.dart';

/// Real implementation against the ASP.NET Core backend (Phase 5).
///
/// It is complete and compiles today; it simply has nothing to talk to until
/// the backend ships. Switching the app over is a single provider override in
/// `lib/core/api/api_providers.dart`.
class HttpWordOsApi implements WordOsApi {
  HttpWordOsApi({
    required String baseUrl,
    required this.tokenReader,
    this.refreshTokenReader,
    this.languageReader,
    this.onRefreshed,
    this.onUnauthorized,
    Dio? dio,
  }) : _dio = dio ??
            Dio(
              BaseOptions(
                baseUrl: baseUrl,
                connectTimeout: const Duration(seconds: 15),
                // Generous: starting a session waits on Gemini.
                //
                // It must stay *above* the backend's own AI budget
                // (`AiServiceOptions.TimeoutSeconds`, 25s), because when the AI
                // is down the backend spends that budget and then builds
                // fallback content. The two used to be equal at 90s, so the
                // client gave up a fraction of a second before the fallback
                // arrived and the learner saw a timeout instead of a lesson.
                receiveTimeout: const Duration(seconds: 90),
                sendTimeout: const Duration(seconds: 30),
                contentType: 'application/json',
                // 4xx/5xx are turned into typed ApiExceptions below rather than
                // being handed to callers as "successful" responses.
                validateStatus: (status) => status != null && status < 400,
              ),
            ) {
    _dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          final token = tokenReader();
          if (token != null && token.isNotEmpty) {
            options.headers['Authorization'] = 'Bearer $token';
          }

          // What the app *says to* the learner follows the language they chose
          // — instructions, feedback — while what it teaches them stays
          // English (ADR-035). Sent per request rather than stored, because the
          // language is a device setting and not an account fact (ADR-010).
          final language = languageReader?.call();
          if (language != null && language.isNotEmpty) {
            options.headers['Accept-Language'] = language;
          }

          handler.next(options);
        },
        onError: (error, handler) async {
          if (error.response?.statusCode != 401) {
            handler.next(error);
            return;
          }

          // An expired access token is an ordinary event, not a sign-out: the
          // refresh token is exchanged once and the original request replayed,
          // so a learner mid-session never loses their place.
          //
          // `_retried` stops a refresh that itself 401s from looping, and the
          // refresh call is deliberately not retried.
          final isRefreshCall = error.requestOptions.path.contains('/auth/');
          final alreadyRetried =
              error.requestOptions.extra['wordos.retried'] == true;

          if (isRefreshCall || alreadyRetried || !await _refresh()) {
            // The refresh token is gone, expired or revoked. Drop everything
            // and let the session controller route back to sign-in.
            onUnauthorized?.call();
            handler.next(error);
            return;
          }

          try {
            final options = error.requestOptions;
            options.extra['wordos.retried'] = true;
            final token = tokenReader();
            if (token != null) options.headers['Authorization'] = 'Bearer $token';
            handler.resolve(await _dio.fetch<dynamic>(options));
          } on DioException catch (e) {
            handler.next(e);
          }
        },
      ),
    );
  }

  final Dio _dio;

  /// Supplies the current JWT; kept as a callback so the API layer never owns
  /// session state.
  final String? Function() tokenReader;

  /// Supplies the refresh token, when there is one.
  final String? Function()? refreshTokenReader;

  /// The language tag the learner is reading the app in, read per request so
  /// switching language in Settings takes effect on the next call.
  final String? Function()? languageReader;

  /// Hands back the rotated pair. The store, not this class, persists them.
  final void Function(String token, String? refreshToken)? onRefreshed;

  /// Invoked when the session cannot be recovered at all.
  final void Function()? onUnauthorized;

  /// Serialises concurrent refreshes: several requests failing at once must
  /// exchange the (single-use) refresh token exactly once between them.
  Future<bool>? _refreshInFlight;

  Future<bool> _refresh() {
    return _refreshInFlight ??= _performRefresh()
      ..whenComplete(() => _refreshInFlight = null);
  }

  Future<bool> _performRefresh() async {
    final refresh = refreshTokenReader?.call();
    if (refresh == null || refresh.isEmpty) return false;

    try {
      // A bare Dio: the interceptor above must not fire for this call.
      final response = await Dio(BaseOptions(
        baseUrl: _dio.options.baseUrl,
        contentType: 'application/json',
        connectTimeout: const Duration(seconds: 15),
      )).post<dynamic>('/auth/refresh', data: {'refreshToken': refresh});

      final body = (response.data as Map?)?.cast<String, dynamic>();
      final token = body?['token'] as String?;
      if (token == null || token.isEmpty) return false;

      onRefreshed?.call(token, body?['refreshToken'] as String?);
      return true;
    } on DioException {
      return false;
    }
  }

  Future<Map<String, dynamic>> _get(String path,
      [Map<String, dynamic>? query]) async {
    final res = await _guard(() => _dio.get<dynamic>(path, queryParameters: query));
    return _asMap(res);
  }

  Future<Map<String, dynamic>> _post(String path, [Object? body]) async {
    final res = await _guard(() => _dio.post<dynamic>(path, data: body));
    return _asMap(res);
  }

  Future<Map<String, dynamic>> _patch(String path, [Object? body]) async {
    final res = await _guard(() => _dio.patch<dynamic>(path, data: body));
    return _asMap(res);
  }

  Future<Map<String, dynamic>> _put(String path, [Object? body]) async {
    final res = await _guard(() => _dio.put<dynamic>(path, data: body));
    return _asMap(res);
  }

  Map<String, dynamic> _asMap(Response<dynamic> res) =>
      (res.data as Map?)?.cast<String, dynamic>() ?? <String, dynamic>{};

  /// Turns every failure into a typed [ApiException].
  ///
  /// Raw transport messages are deliberately **not** forwarded to the UI: they
  /// carry the base URL, internal host names and sometimes response bodies,
  /// which is information disclosure in a user-visible string. The `code` is
  /// what callers branch on; the message is a plain sentence the UI can show
  /// (`docs/07-SECURITY.md` §9).
  Future<Response<dynamic>> _guard(
    Future<Response<dynamic>> Function() call,
  ) async {
    try {
      return await call();
    } on DioException catch (e) {
      final status = e.response?.statusCode;
      final data = e.response?.data;

      // A structured error from our own backend is the one case where the
      // message is safe to show — the backend authored it for a user.
      if (data is Map && data['error'] is Map) {
        final error = (data['error'] as Map).cast<String, dynamic>();
        throw ApiException(
          error['code'] as String? ?? 'UNKNOWN',
          error['message'] as String? ?? 'Request failed.',
          statusCode: status,
        );
      }

      // ASP.NET validation failures arrive as RFC 9110 ProblemDetails, with the
      // offending fields under `errors`. Flattening them here is what lets a
      // form say which field is wrong instead of "request failed".
      if (data is Map && data['errors'] is Map) {
        final fields = (data['errors'] as Map).cast<String, dynamic>();
        final messages = fields.values
            .whereType<List<dynamic>>()
            .expand((e) => e)
            .whereType<String>()
            .toList();

        throw ApiException(
          'VALIDATION_FAILED',
          messages.isEmpty ? 'Please check what you entered.' : messages.first,
          statusCode: status,
          fieldErrors: {
            for (final entry in fields.entries)
              entry.key: (entry.value as List<dynamic>? ?? const [])
                  .whereType<String>()
                  .toList(),
          },
        );
      }

      throw switch (e.type) {
        DioExceptionType.connectionTimeout ||
        DioExceptionType.sendTimeout ||
        DioExceptionType.receiveTimeout =>
          ApiException('TIMEOUT', 'The server took too long to respond.',
              statusCode: status),
        DioExceptionType.connectionError =>
          ApiException('NETWORK', 'No connection. Check your network.',
              statusCode: status),
        DioExceptionType.cancel =>
          ApiException('CANCELLED', 'Request cancelled.', statusCode: status),
        DioExceptionType.badCertificate => const ApiException(
            'INSECURE_CONNECTION',
            'The connection could not be trusted.',
          ),
        // 401 and 403 arrive with an empty body — the framework's own auth
        // challenge, not one of our error envelopes — so they are recognised by
        // status alone.
        _ => switch (status) {
            401 => ApiException('UNAUTHORIZED', 'Please sign in again.',
                statusCode: status),
            403 => ApiException(
                'FORBIDDEN', 'You do not have access to this.',
                statusCode: status),
            404 => ApiException('NOT_FOUND', 'That is no longer available.',
                statusCode: status),
            409 => ApiException('CONFLICT', 'That is not available right now.',
                statusCode: status),
            // Rate limited. The budget is per user and per minute, so waiting
            // genuinely resolves it — say so rather than showing a failure.
            429 => ApiException(
                'RATE_LIMITED',
                'Too many requests. Please wait a moment and try again.',
                statusCode: status),
            _ => ApiException(
                status != null && status >= 500 ? 'SERVER_ERROR' : 'UNKNOWN',
                status != null && status >= 500
                    ? 'Something went wrong on our side. Please try again.'
                    : 'Request failed. Please try again.',
                statusCode: status),
          },
      };
    } on FormatException {
      // The body was not the JSON we expect — a proxy error page, a truncated
      // response, or a contract mismatch. Never let it surface as a raw crash.
      throw const ApiException(
        'BAD_RESPONSE',
        'The server sent something we could not read.',
      );
    }
  }

  @override
  Future<AuthResponse> register({
    required String email,
    required String password,
    required String displayName,
    String? phoneCountryCode,
    String? phoneNumber,
  }) async =>
      AuthResponse.fromJson(await _post('/auth/register', {
        'email': email,
        'password': password,
        'displayName': displayName,
        if (phoneCountryCode != null && phoneCountryCode.isNotEmpty)
          'phoneCountryCode': phoneCountryCode,
        if (phoneNumber != null && phoneNumber.isNotEmpty)
          'phoneNumber': phoneNumber,
      }));

  @override
  Future<AuthResponse> login({
    required String email,
    required String password,
  }) async =>
      AuthResponse.fromJson(
          await _post('/auth/login', {'email': email, 'password': password}));

  @override
  Future<void> logout() async {
    await _post('/auth/logout');
  }

  @override
  Future<UserProfile> me() async => UserProfile.fromJson(await _get('/me'));

  @override
  Future<List<InterestOption>> interestOptions() async {
    final res = await _guard(() => _dio.get<dynamic>('/onboarding/interests'));
    return (res.data as List<dynamic>)
        .map((e) => InterestOption.fromJson((e as Map).cast<String, dynamic>()))
        .toList();
  }

  @override
  Future<UserProfile> saveInterests(List<String> interests) async =>
      UserProfile.fromJson(await _put('/me/interests', {'interests': interests}));

  @override
  Future<PlacementStep> startPlacement() async =>
      PlacementStep.fromJson(await _post('/placement/start', const {}));

  @override
  Future<PlacementStep> answerPlacement({
    required String sessionId,
    required String itemId,
    required String answer,
  }) async =>
      PlacementStep.fromJson(await _post('/placement/$sessionId/answer', {
        'itemId': itemId,
        'answer': answer,
      }));

  @override
  Future<PlacementResult> completePlacement(String sessionId) async =>
      PlacementResult.fromJson(
          await _post('/placement/$sessionId/complete', const {}));

  @override
  Future<HubState> hub() async => HubState.fromJson(await _get('/hub'));

  @override
  Future<List<WordCandidate>> lookupWord(String query) async {
    final res = await _guard(
      () => _dio.get<dynamic>('/words/lookup', queryParameters: {'q': query}),
    );
    return (res.data as List<dynamic>)
        .map((e) => WordCandidate.fromJson((e as Map).cast<String, dynamic>()))
        .toList();
  }

  @override
  Future<WordDefinition> defineWord(String word) async {
    final res = await _guard(
      () => _dio.get<dynamic>('/words/define', queryParameters: {'w': word}),
    );
    return WordDefinition.fromJson((res.data as Map).cast<String, dynamic>());
  }

  @override
  Future<Word> addWord(WordCandidate candidate) async =>
      // The sense id is the whole request. Sending the level, meaning or
      // definition would be pointless — the server re-resolves the sense
      // against the lexicon and stores its own row (ADR-012) — and sending them
      // anyway would suggest the client has a say in what a word means.
      Word.fromJson(await _post('/words', {
        'senseId': candidate.senseId,
        'text': candidate.text,
        'meaning': candidate.meaning,
      }));

  @override
  Future<WordPage> words({
    WordState? state,
    int page = 0,
    String? query,
  }) async =>
      WordPage.fromJson(await _get('/words', {
        if (state != null) 'state': state.wire,
        if (query != null && query.trim().isNotEmpty) 'q': query.trim(),
        'page': page,
      }));

  @override
  Future<WordDetail> wordDetail(String wordId) async =>
      WordDetail.fromJson(await _get('/words/$wordId'));

  @override
  Future<SkillSession> startSession(
    SkillType skill, {
    bool practice = false,
  }) async =>
      SkillSession.fromJson(await _post(
        '/sessions/${skill.wire.toLowerCase()}/start'
        '${practice ? '?practice=true' : ''}',
      ));

  @override
  Future<AnswerResult> submitAnswer({
    required String sessionId,
    required String itemId,
    required String answer,
    int? timeMs,
  }) async =>
      AnswerResult.fromJson(await _post('/sessions/$sessionId/answer', {
        'itemId': itemId,
        'answer': answer,
      }));

  @override
  Future<WarmupResult> answerWarmup({
    required String sessionId,
    required String wordId,
    required String answer,
  }) async =>
      WarmupResult.fromJson(
          await _post('/sessions/$sessionId/warmup/answer', {
        'wordId': wordId,
        'answer': answer,
      }));

  @override
  Future<SkillSession> resumeSession(String sessionId) async =>
      SkillSession.fromJson(await _get('/sessions/$sessionId'));

  @override
  Future<SkillSession> changeSessionLevel(
    String sessionId,
    CefrLevel level,
  ) async =>
      SkillSession.fromJson(await _post('/sessions/$sessionId/level', {
        'level': level.wire,
      }));

  @override
  Future<WritingEvaluation> submitWriting({
    required String sessionId,
    required String itemId,
    required String sentence,
  }) async =>
      WritingEvaluation.fromJson(await _post('/sessions/$sessionId/writing', {
        'itemId': itemId,
        'answer': sentence,
      }));

  @override
  Future<SpeakingTurn> submitSpeakingTurn({
    required String sessionId,
    required String transcript,
  }) async =>
      SpeakingTurn.fromJson(await _post(
        '/sessions/$sessionId/speaking/turn',
        {'transcript': transcript},
      ));

  @override
  Future<SessionResult> completeSession(String sessionId) async =>
      SessionResult.fromJson(await _post('/sessions/$sessionId/complete'));

  @override
  Future<void> abandonSession(String sessionId) async {
    await _post('/sessions/$sessionId/abandon');
  }

  @override
  Future<WeeklyReviewSession> startWeeklyReview() async =>
      WeeklyReviewSession.fromJson(await _post('/weekly-review/start'));

  @override
  Future<ReviewAnswerResult> answerWeeklyReview({
    required String reviewId,
    required String itemId,
    required String answer,
  }) async =>
      ReviewAnswerResult.fromJson(
        await _post('/weekly-review/$reviewId/answer', {
          'itemId': itemId,
          'answer': answer,
        }),
      );

  @override
  Future<WeeklyReviewResult> completeWeeklyReview(String reviewId) async =>
      WeeklyReviewResult.fromJson(
          await _post('/weekly-review/$reviewId/complete'));

  @override
  Future<SkillLevel> updateSkillLevel({
    required SkillType skill,
    required CefrLevel level,
  }) async =>
      SkillLevel.fromJson(await _patch('/settings/skill-level', {
        'skill': skill.wire,
        'level': level.wire,
      }));

  @override
  Future<SkillLevel> updateDailyTarget({
    required SkillType skill,
    required int target,
  }) async =>
      SkillLevel.fromJson(await _patch('/settings/daily-target', {
        'skill': skill.wire,
        'target': target,
      }));

  @override
  Future<PublicConfig> config() async =>
      PublicConfig.fromJson(await _get('/config'));

  @override
  Future<AdminOverview> adminOverview({int? days}) async =>
      AdminOverview.fromJson(await _get('/admin/overview', {'days': ?days}));

  @override
  Future<AdminUserPage> adminUsers({
    String? query,
    int? days,
    int page = 0,
  }) async =>
      AdminUserPage.fromJson(await _get('/admin/users', {
        if (query != null && query.trim().isNotEmpty) 'q': query.trim(),
        'days': ?days,
        'page': page,
      }));

  @override
  Future<AdminUserDetail> adminUserDetail(String userId) async =>
      AdminUserDetail.fromJson(await _get('/admin/users/$userId'));

  @override
  Future<void> sendFeedback(String body) async {
    // The build travels with the message so the Owner is not left guessing
    // which version "it crashed" happened on (ADR-053).
    await _post('/feedback', {
      'body': body,
      'appVersion': AppEnvironment.version,
      'platform': AppEnvironment.platformName,
    });
  }

  @override
  Future<FeedbackPage> adminFeedback({bool? handledOnly, int page = 0}) async {
    final status = handledOnly == null
        ? ''
        : '&status=${handledOnly ? 'HANDLED' : 'NEW'}';

    return FeedbackPage.fromJson(
        await _get('/admin/feedback?page=$page&pageSize=50$status'));
  }

  @override
  Future<void> adminSetFeedbackHandled(String id, bool handled) async {
    await _patch('/admin/feedback/$id', {'handled': handled});
  }

  @override
  Future<ScheduleAdvance> adminAdvanceSchedule(
    String userId, {
    int days = 2,
  }) async =>
      ScheduleAdvance.fromJson(await _post(
        '/admin/users/$userId/advance-schedule',
        {'days': days},
      ));

  @override
  Future<AdminWordPage> adminUserWords(
    String userId, {
    WordState? state,
    String? query,
    int page = 0,
  }) async =>
      AdminWordPage.fromJson(await _get('/admin/users/$userId/words', {
        if (state != null) 'state': state.wire,
        if (query != null && query.trim().isNotEmpty) 'q': query.trim(),
        'page': page,
      }));

  @override
  Future<AdminWordJourney> adminWordJourney(String wordId) async =>
      AdminWordJourney.fromJson(await _get('/admin/words/$wordId'));

  @override
  Future<PlacementEvidence> adminPlacementEvidence(String userId) async =>
      PlacementEvidence.fromJson(
          await _get('/admin/users/$userId/placement'));
}
