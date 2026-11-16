# 04 — الأمن والخصوصية

## 1. الخلاصة

- **الحملة:** `20261111-0121-d365f09` · HEAD `d365f09` · محلي، PostgreSQL حقيقي، والـ pipeline الكامل: المصادقة والصلاحيات والـ rate limiter.
- **النطاق المقترح:** ASVS 5.0 Level 2 كنطاق استرشادي، وAPI Security Top 10 2023، وMASVS 2.x. هذا ليس ادعاء امتثال.
- **النتيجة:** **اجتاز النطاق المنفذ مع عيبين غير مانعين.**
- **ما ثبت:**
  - طالب B ما يوصل لأي مورد يخص الطالب A في 10 مسارات: كلمة وجلسة واختبار تحديد المستوى، قراءة وتعديل وحذف. والقاعدة لم تتغير.
  - مصفوفة الأدوار: الزائر يأخذ 401، والطالب 403 على الإدارة، والمحلل 200 على صفحات التحليل و403 على صفحات المالك وسجل التدقيق وتخطي الجدول.
  - المحلل ما يستلم إيميل الطالب ولا رقم جواله في 6 ردود مختلفة، والمالك يستلمها (عينة ضابطة).
  - JWT: كل هذي الحالات تُرفض بـ 401: مفتاح بيئة ثانية، وتعديل الدور مع التوقيع الأصلي، و`alg:none`، وtoken منتهي، وissuer خطأ، وaudience خطأ.
  - حقن `role:Owner` في التسجيل يُتجاهل، ومستوى أو تعريف مزوّر مع sense حقيقي يُتجاهل.
  - الجلسة المستأنفة لا تحمل مفتاح الإجابة.
  - محادثات WordOS AI معزولة لكل مدير.
  - خدمة الذكاء الاصطناعي ترفض بدون `X-Service-Token`، وتقارن بزمن ثابت، وترفض التشغيل بلا token إلا بإعداد صريح.
- **ما لم يثبت:**
  - سلوك Render مع ترويسة `True-Client-IP` يرسلها العميل (انظر 08).
  - فحص الـ APK المبني: حفظ الـ tokens فعلياً، والنسخ الاحتياطي، والـ logs.
  - CSP على صفحة الإدارة في متصفح حقيقي.
- **قرار النشر لهذا النطاق:** لا مانع مثبت.

### نموذج التهديد المختصر

```
هاتف ─HTTPS/JWT─► API (Render) ─loopback+X-Service-Token─► ai-service ─► Gemini/Groq/Edge
                       │
                       └─TLS─► Neon (دور wordos_app بلا DDL)
متصفح الإدارة ─HTTPS/JWT في sessionStorage─► /admin (نفس الأصل، CSP)
```
الأصول المحمية: تقدّم الطالب وبياناته (الإيميل والجوال والملاحظات)، ومفاتيح المزودين، وتكلفة الذكاء الاصطناعي، ودور المالك.

## 2. النطاق والنتائج

| المتطلب | الاختبار / الفحص | الطبقة | النتيجة | الدليل |
|---|---|---|---|---|
| عزل الطلاب (BOLA) في 10 مسارات + فحص القاعدة | `Qa/SecurityQaTests.S01` | API+PG | PASS | كلها 404، إلا `abandon` يرد 204 كعملية idempotent بلا أثر. القاعدة لم تتغير |
| مصفوفة الأدوار (4 أدوار × 12 مسار) | `S02` | API+PG | PASS | `raw/full-run-console.txt` |
| المحلل بلا بيانات اتصال | `S03` + `AdminIntelTests.An_analyst_reads_every_figure…` | API+PG | PASS | |
| تلاعب JWT (7 حالات) | `S04` | API | PASS | |
| Mass assignment | `S05` | API+PG | PASS | |
| لا مفتاح إجابة قبل الوقت | `S06` + `SessionTests.The_correct_answer_is_never_sent…` | API | PASS | |
| عزل محادثات الإدارة | `S07` | API+PG | PASS | |
| تجديد الـ token: الاستعمال مرة واحدة تحت التزامن | `Qa/ConcurrencyQaTests.C06` | API+PG | **FAIL** | D04-1 |
| ملكية المعرّفات في الأحداث | `Qa/AdminNumbersQaTests.A03` | API+PG | **FAIL** | D04-2 |
| Service token | `ai-service/tests/test_auth.py` (9) | pytest | PASS | 243/243 في `ai-service` |
| حدود المحاولات واستهلاك رمز الاستعادة | `PasswordResetTests` الموجودة | API+PG | PASS | |
| أسرار في الكود الحالي وتاريخ git | سكربت regex يحجب القيم (7 أنماط) | ساكن | PASS | إصابة وحيدة في `docs/09-DEPLOYMENT.md:57` مثال وهمي (المضيف `ep-cool-name-123456`). `.env` و`key.properties` مستثناة في gitignore |
| ثغرات المكتبات (NuGet) | `dotnet list package --vulnerable --include-transitive` | ساكن | PASS | 0 في 7 مشاريع |
| ثغرات المكتبات (npm، الإنتاج) | `npm audit --omit=dev` | ساكن | PASS | 0 |
| ثغرات npm (أدوات التطوير) | `npm audit` | ساكن | ملاحظة | `vite` 8.3.0–8.3.2: high، أداة تطوير لا تُشحن (O04-2) |
| ثغرات Python | `pip-audit` | — | NOT_RUN | الأداة غير مثبتة، وما ثبّتها |
| XSS في الإدارة | grep لـ `dangerouslySetInnerHTML` و`innerHTML` و`eval`، وقراءة `Markdown.tsx` | ساكن | PASS | React يهرّب النص، وعارض Markdown يبني عناصر لا HTML، وروابط `wa.me` و`mailto` تُبنى على الخادم من رقم مُتحقَّق بـ regex |
| ترويسات الإدارة | `Program.cs:515–524` | ساكن | PASS | CSP `default-src 'self'; script-src 'self'; frame-ancestors 'none'`، و`X-Frame-Options DENY`، و`nosniff` |
| JWT في العميل | `token_store.dart` (flutter_secure_storage)، والحذف عند الخروج | ساكن | PASS | |
| Android | `AndroidManifest.xml` | ساكن | ملاحظة | `allowBackup` غير محدد (افتراضياً true) (O04-3) |
| iOS ATS | `Info.plist` | ساكن | PASS | cleartext للعناوين المحلية فقط |

**الإجمالي:** planned 20؛ PASS 15؛ FAIL 2؛ ملاحظات 2؛ NOT_RUN 1.

## 3. العيوب المثبتة

| ID | الوصف والأثر | الشدة | قرار النشر | الثقة | الإعادة | إصلاح مقترح |
|---|---|---|---|---|---|---|
| **D04-1** | **سباق في تدوير refresh token:** نفس الـ token يُقدَّم 10 مرات بنفس اللحظة فيصدر 10 خلفاء صالحين بدون ما يشتغل كشف إعادة الاستخدام. السبب: قراءة `UsedAt` ثم كتابتها بلا قفل أو شرط (`AuthEndpoints.cs:249–345`). الاستغلال يحتاج token مسروق ومسابقة صاحبه بالمللي ثانية، وبعدها يحتفظ المهاجم بجلسة 30 يوماً لا يكشفها التدوير. | Medium | يحتاج إصلاحاً غير مانع | مؤكد | `C06` | `UPDATE … SET "UsedAt"=now WHERE "Id"=@id AND "UsedAt" IS NULL` وفحص عدد الصفوف. الخاسر يُعامل كإعادة ضمن الـ grace |
| **D04-2** | **`POST /api/events` لا يتحقق من ملكية `wordId` و`sessionId`:** الطالب A أرسل حدثاً بمعرّف كلمة الطالب B فقُبل وخُزّن. لا يكشف بيانات، والمعرّفات GUIDv7 صعبة التخمين، لكنه يلوّث قياسات الإدارة المرتبطة بالكلمة أو الجلسة. | Low | يحتاج إصلاحاً غير مانع | مؤكد | `A03` | تصفية المعرّفات غير المملوكة (إسقاطها أو تعيينها null) قبل الحفظ |

### ملاحظات وتحسينات (ليست عيوباً)

- **O04-1:** الدور محفوظ داخل JWT لمدة 15 دقيقة. Owner سُحب دوره يبقى يشوف الإدارة حتى ينتهي الـ token. هذا مقبول مع `AccessTokenMinutes=15`، ويُذكر للعلم.
- **O04-2:** حدّث `vite` في أدوات التطوير لما تتوفر نسخة مصلحة.
- **O04-3:** حدد `android:allowBackup="false"` أو `dataExtractionRules` في Android. الـ tokens مشفرة بمفتاح الجهاز، لكن الأفضل منع النسخ الاحتياطي.
- **O04-4:** `ai-service/.env` صلاحياته `644` محلياً، والـ README يقول `600`. هذا على جهاز التطوير فقط.
- **O04-5:** `/api/auth/refresh` يشترك مع login في حد 10 لكل IP كل 15 دقيقة. هذا قرار تصميم ضد credential stuffing، لكن أثره على الاستخدام في 08 (D08-1).
- **الخصوصية:**
  - أدوات WordOS AI لا تمرر للنموذج ما يعرّف الطالب. هذا مثبت في `AdminAgentTests.Learners_go_to_the_screen_and_never_to_the_model`.
  - مسارات التدريس ترسل للنموذج كتابة الطالب ونص كلامه، بلا اسم ولا إيميل، حسب قراءة `prompts.py` وطلبات الـ stub.
  - **حذف الحساب كاملاً غير موجود**: حذف الكلمة منطقي فقط، ولا يوجد مسار يحذف بيانات الحساب. هذا متطلب مستقل لم تُطلب تغطيته، ويستحق قراراً قبل النشر للعامة.

## 5. النواقص

| ما لم يُختبر | السبب | الأثر | أقل إجراء |
|---|---|---|---|
| تجاوز حد الدخول بتزوير `True-Client-IP` خلف Render | لا وصول لبيئة Render | **متوسط** | طلبان لـ `/health/live` بترويسة مزوّرة على الإنتاج، ومقارنة `ClientAddress.Describe` في سجل الرفض |
| فحص الـ APK (MASTG: التخزين والـ logs والـ backup) | يحتاج جهاز وأدوات | متوسط | `adb backup` وفحص `logcat` بعد الدخول والخروج |
| `pip-audit` | غير مثبت | منخفض | `pip install pip-audit` في venv منفصل |
| CSP في متصفح حقيقي | الوقت | منخفض | فتح `/admin` وقراءة الترويسات |

## 6. إعادة التشغيل

```bash
cd backend && dotnet test tests/WordOs.Api.Tests --filter "FullyQualifiedName~Qa.SecurityQaTests"
cd ai-service && .venv/bin/python -m pytest -q
cd admin-web && npm audit --omit=dev
```
سكربت فحص الأسرار مضمّن في `../README.md`، وهو يطبع المسار ونوع النمط وأول 10 أحرف من SHA-256 فقط.
