# 10 — Render وNeon المجانيان: الحصة الشهرية والاستدامة

طبّق `00-COMMON.md`. المالك أكد أن **Render وNeon كلاهما Free** وأنه تلقى تنبيه اقتراب حصة شهرية، لكنه لم يحدد مصدر التنبيه أو اسم العداد. لا تفترض أن السبب كثرة المستخدمين أو خطأ برمجي. المطلوب تشخيص بدليل، وتقدير استدامة الحمل، وقد تكون النتيجة أن الاستهلاك طبيعي والباقة تكفي.

**اقرأ:** `Dockerfile` و`docker/entrypoint.sh` و`docs/09-DEPLOYMENT.md` وADR-062/077، `Program.cs` و`ReadinessProbe.cs` وCapacityOptions وIntelDataset، AI/tts/groq وanalytics والإشعارات. الاستضافة واحدة للـAPI وPython حسب build؛ تحقق مما نُشر بالفعل.

## 1. تحديد الحصة دون صرفها

اطلب أو اقرأ من وصول مصرح به: نص التنبيه وتاريخه، صفحة Usage/Billing، plan الفعلي، دورة reset، اسم العداد ووحدته، limit/used/remaining، النطاق project/workspace/account، جميع الخدمات والفروع الأخرى المشاركة. لا تطلب مفاتيح في الدردشة. إذا لم يتوفر الوصول اترك خلايا الحساب `UNKNOWN`، وأنجز التحليل الساكن وقالب التقدير؛ لا تخترع نسبة أو تاريخ نفاد.

في Render افصل instance-hours عن outbound bandwidth وعن build pipeline minutes. في Neon افصل CU-hours عن storage وعن network transfer، واحصر branches/read replicas/computes. افحص انتهاء الحصة: إيقاف خدمة، منع builds، أو تكلفة إضافية حسب الباقة والدفع؛ ليست كل رسالة معناها انهيار التطبيق.

تحقق وقت التنفيذ من [Render Free](https://render.com/docs/free) و[Neon Plans](https://neon.com/docs/introduction/plans) و[Neon Usage](https://neon.com/docs/introduction/monitor-usage)، واربط كل حد بالرابط والتاريخ وبحساب المستخدم. لا تنقل «100 ساعة» من تعليق قديم وتخلطها مع CU-hours، ولا تعتمد مقالًا أقدم يناقض لوحة الحساب.

## 2. فحص الاستهلاك الخامل

- استخرج health-check path في Render وexternal uptime monitors وcron وأي تبويب admin auto-refresh أو job يقرأ DB. اربط timestamps مع compute active intervals وquery counters، لا مع عدد مستخدمين تقريبي.
- `/health/live` في الكود لا يقرأ DB. `/health/ready` يقرأها عند انتهاء cache نجاح افتراضي 3600s ويكشف `databaseCheckedSecondsAgo`. تأكد من commit المنشور والقيمة الفعلية وعدد replicas/restarts التي تصفر cache. الـcache ناجح فقط؛ تكرار الفشل له نمط آخر.
- اختبر محليًا عدد DB probes أثناء burst وعند expiry. على Neon راقب دخول idle/scale-to-zero من لوحة المزود أو telemetry غير الموقظة؛ لا تستخدم SQL دوريًا كل دقيقة لقياس النوم ثم تستنتج أن DB لا تنام.
- قارن baseline خامل بلا monitors خارجية مع الإعداد الحالي فقط في بيئة اختبار مناسبة. لا تعطّل مراقبة الإنتاج تلقائيًا. فصل live/ready يوازن سرعة اكتشاف عطل DB مع الكلفة؛ نجاح ready المخزن قد يكون قديمًا، فاذكر هذه المقايضة.
- live ping قد يترك Render مستيقظًا حتى لو Neon نامت. لا تساوِ بين health checks الداخلية للمنصة وطلبات keep-alive الخارجية. راجع [Render health checks](https://render.com/docs/health-checks) و[Neon scale-to-zero](https://neon.com/docs/introduction/scale-to-zero).

## 3. مصادر الاستهلاك المرتبطة بالتطبيق

قِس استهلاك idle، ثم رحلة lookup/list، ثم درس دون AI، ثم درس AI حقيقي محدود، ثم صوت، ثم admin report. سجّل cache warm/cold وtimeouts/retries وpreloading؛ حجم JSON/base64 مقابل الصوت، إعادة توليد passage وglossary repair، عدد model calls لكل رحلة، حفظ events ونمو الجداول/indexes، وقت builds وعددها، والبيانات التي تحملها تقارير الإدارة. هل خرجت bytes إلى العميل أو إلى مزود خارجي أو بين الخدمات؟ انسبها للعداد الصحيح وتجنب double count.

لا تعتبر Groq fallback إلى Gemini مجانيًا لأن الاستضافة مجانية. تكاليف Gemini/Groq/STT/TTS والبريد تبقى أعمدة مستقلة، وتتحقق أسعارها وحصصها من المزود عند الحاجة. الطلب الذي فشل بعد تنفيذ provider قد يستهلك أيضًا.

## 4. الحسابات المطلوبة

أنشئ نموذجًا CSV/JSON أو جدولًا قابلًا لإعادة الحساب مع وحدات ومصادر وافتراضات واضحة:

```text
Render instance-hours = مجموع ساعات تشغيل كل instance محتسبة في workspace
Neon CU-hours = مجموع (CU الفعالة خلال interval × ساعات interval) لكل compute محتسب
remaining = limit − used
quota_percent = 100 × used / limit
days_remaining_at_current_rate = remaining / recent_units_per_day  [إن كان المعدل > 0]
forecast_at_reset = used + expected_remaining_period_usage
sessions_per_month = DAU × sessions_per_active_user_per_day × days_in_period
variable_bytes = مجموع (عدد الرحلات من النوع × bytes المقاسة لهذا النوع)
projected_storage = current_storage + measured_daily_growth × remaining_days
```

قدّر CU-hours من duty cycle وحجم compute الفعلي، لا من عدد queries وحده ولا بجمع «كلفة query» إذا تداخلت نوافذ الاستيقاظ. استخدم active intervals المتحدّة؛ scale-to-zero يحتاج فترة خمول بعد آخر نشاط. فرّق نمو storage عن GB-month المحتسبة إن كانت ذات صلة. التوقع الخطي بسيط وليس يقينًا؛ أعط low/base/high عند تباين الاستخدام.

مثال **حسابي فقط** لا يصف الباقة: compute ثابت 0.25 CU يعمل 24 ساعة ×30 يومًا يستهلك 180 CU-hour؛ يعمل 8 ساعات يوميًا يستهلك 60. Instance واحدة 24×30=720 instance-hour في Render، لكن bytes/builds والخدمات الأخرى لها عدادات مستقلة. لا تقول إن مثالًا يثبت أن حساب المالك سينفد.

ابنِ سيناريوهات ذروة 100/300/500 بالتزامن، مع افتراضات صريحة لعدد مرات الذروة ومدتها وDAU والجلسات/يوم وأيام النشاط. **300 متزامن لا يحدد الاستهلاك الشهري وحده**. سلّم «المتاح من الحصة / المعدل المقاس / التوقع حتى reset / هامش الأمان» لكل مورد، وعدد الأيام أو الرحلات الممكنة ضمن الفرضيات فقط.

## 5. قرار متوازن

صنّف السبب: مثبت / مرجح يحتاج قياسًا / غير معلوم. إذا لا يوجد هدر فقل ذلك. إن وُجد، اقترح أقل تغيير يخفض الاستهلاك مع أثره على startup/availability، ودليل before/after مطلوب. لا توصي بالترقية تلقائيًا؛ قارن استمرار المجاني كما هو، ضبط منخفض الأثر، أو باقة أخرى عند وجود قياس يبررها. عدم SLA أو دعم production في المجاني قيد خدمة يُذكر، وليس bug في التطبيق.

أخرج تقريرًا يجيب: أي عداد أرسل التنبيه؟ ما المستهلك الحقيقي؟ هل القاعدة مستيقظة بلا طلبات متعلمين؟ هل الذروة المقاسة ممكنة؟ هل الشهر ممكن؟ ومتى نتوقع الاقتراب من الحد؟ إذا ينقص الدليل قل بالضبط ما الذي يلزم. لا تغيّر خطة أو فاتورة أو monitoring أو أسرارًا ضمن هذا التدقيق.

احفظ `quota-baseline.csv` و`budget-model.json` و`usage-forecast.csv` بأعمدة مصدر/وحدة/تاريخ/ثقة، وأرفق raw usage snapshots محجوبة عند توفرها. قبل أي load حي اكتب ميزانية تلك الجولة بالأعداد والمدة والحصة، واجعل السكربت يتوقف عندها.
