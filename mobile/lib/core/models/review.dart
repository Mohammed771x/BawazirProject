/// Weekly Review models.
///
/// The weekly review measures retention only — it never changes pipeline state
/// (rule R9). Wrong answers go back to the end of the queue until cleared.
library;

class ReviewItem {
  const ReviewItem({
    required this.id,
    required this.wordId,
    required this.prompt,
    required this.options,
  });

  final String id;
  final String wordId;
  final String prompt;
  final List<String> options;

  factory ReviewItem.fromJson(Map<String, dynamic> json) => ReviewItem(
        id: json['id'] as String,
        wordId: json['wordId'] as String,
        prompt: json['prompt'] as String? ?? '',
        options: (json['options'] as List<dynamic>? ?? const []).cast<String>(),
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'wordId': wordId,
        'prompt': prompt,
        'options': options,
      };
}

class WeeklyReviewSession {
  const WeeklyReviewSession({
    required this.id,
    required this.periodStart,
    required this.totalWords,
    required this.queue,
  });

  final String id;
  final DateTime? periodStart;
  final int totalWords;
  final List<ReviewItem> queue;

  factory WeeklyReviewSession.fromJson(Map<String, dynamic> json) =>
      WeeklyReviewSession(
        id: json['id'] as String,
        periodStart:
            DateTime.tryParse(json['periodStart'] as String? ?? '')?.toUtc(),
        totalWords: (json['totalWords'] as num?)?.toInt() ?? 0,
        queue: (json['queue'] as List<dynamic>? ?? const [])
            .map((e) => ReviewItem.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'periodStart': periodStart?.toIso8601String(),
        'totalWords': totalWords,
        'queue': queue.map((e) => e.toJson()).toList(),
      };
}

class ReviewAnswerResult {
  const ReviewAnswerResult({
    required this.itemId,
    required this.isCorrect,
    required this.correctAnswer,
    required this.requeued,
    required this.remaining,
    required this.nextItem,
  });

  final String itemId;
  final bool isCorrect;
  final String correctAnswer;
  final bool requeued;
  final int remaining;
  final ReviewItem? nextItem;

  factory ReviewAnswerResult.fromJson(Map<String, dynamic> json) =>
      ReviewAnswerResult(
        itemId: json['itemId'] as String,
        isCorrect: json['isCorrect'] as bool? ?? false,
        correctAnswer: json['correctAnswer'] as String? ?? '',
        requeued: json['requeued'] as bool? ?? false,
        remaining: (json['remaining'] as num?)?.toInt() ?? 0,
        nextItem: json['nextItem'] == null
            ? null
            : ReviewItem.fromJson(json['nextItem'] as Map<String, dynamic>),
      );

  Map<String, dynamic> toJson() => {
        'itemId': itemId,
        'isCorrect': isCorrect,
        'correctAnswer': correctAnswer,
        'requeued': requeued,
        'remaining': remaining,
        'nextItem': nextItem?.toJson(),
      };
}

class WeeklyReviewResult {
  const WeeklyReviewResult({
    required this.reviewId,
    required this.totalWords,
    required this.firstPassCorrect,
    required this.weeklyScore,
    required this.totalAttempts,
  });

  final String reviewId;
  final int totalWords;
  final int firstPassCorrect;
  final double weeklyScore;
  final int totalAttempts;

  factory WeeklyReviewResult.fromJson(Map<String, dynamic> json) =>
      WeeklyReviewResult(
        reviewId: json['reviewId'] as String,
        totalWords: (json['totalWords'] as num?)?.toInt() ?? 0,
        firstPassCorrect: (json['firstPassCorrect'] as num?)?.toInt() ?? 0,
        weeklyScore: (json['weeklyScore'] as num?)?.toDouble() ?? 0,
        totalAttempts: (json['totalAttempts'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'reviewId': reviewId,
        'totalWords': totalWords,
        'firstPassCorrect': firstPassCorrect,
        'weeklyScore': weeklyScore,
        'totalAttempts': totalAttempts,
      };
}
