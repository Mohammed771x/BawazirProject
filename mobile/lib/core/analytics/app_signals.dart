import 'dart:async';

/// Things the app's lower layers notice and the tracker should hear about,
/// without either side importing the other (ADR-125).
///
/// The HTTP client cannot depend on the tracker — the tracker sends through
/// the HTTP client — and the notification plugin's tap callback has no
/// provider container at all. Both just announce here; the app root listens.
abstract final class AppSignals {
  static final StreamController<ApiFailureSignal> _apiFailures =
      StreamController<ApiFailureSignal>.broadcast();
  static final StreamController<void> _notificationTaps =
      StreamController<void>.broadcast();

  static Stream<ApiFailureSignal> get apiFailures => _apiFailures.stream;
  static Stream<void> get notificationTaps => _notificationTaps.stream;

  static void apiFailed(String code, String path, int? status) =>
      _apiFailures.add(ApiFailureSignal(code, path, status));

  static void notificationTapped() => _notificationTaps.add(null);
}

class ApiFailureSignal {
  const ApiFailureSignal(this.code, this.path, this.status);

  final String code;

  /// The route with ids replaced, so `/sessions/:id/answer` groups as one.
  final String path;
  final int? status;
}
