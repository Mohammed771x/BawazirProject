select t, n, md5 from (
 select 'users' t, count(*) n, md5(string_agg(u::text, '|' order by "Id")) md5 from users u union all
 select 'words', count(*), md5(string_agg(w::text, '|' order by "Id")) from words w union all
 select 'word_skill_states', count(*), md5(string_agg(s::text, '|' order by "WordId","Skill")) from word_skill_states s union all
 select 'word_events', count(*), md5(string_agg(e::text, '|' order by "Id")) from word_events e union all
 select 'skill_sessions', count(*), md5(string_agg(x::text, '|' order by "Id")) from skill_sessions x union all
 select 'refresh_tokens', count(*), md5(string_agg(r::text, '|' order by "Id")) from refresh_tokens r union all
 select 'weekly_reviews', count(*), md5(string_agg(r::text, '|' order by "Id")) from weekly_reviews r union all
 select 'feedback_messages', count(*), md5(string_agg(f::text, '|' order by "Id")) from feedback_messages f
) z order by t;
