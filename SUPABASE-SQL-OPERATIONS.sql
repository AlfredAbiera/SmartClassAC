-- SmartClass AC / optional Supabase SQL Editor operations
-- Use the Admin dashboard for normal work. These statements are only for an
-- administrator validating data or performing a controlled DBA operation.

-- 1) Inspect existing teacher accounts and classrooms.
select id, display_name, username, email
from public.user_accounts
where role = 'Teacher' and is_active = true
order by display_name;

select id, name, capacity, target_temperature, current_temperature, ac_status
from public.classrooms
where is_active = true
order by name;

-- 2) Create a classroom if an authorized database administrator needs to do it
-- outside the dashboard. The unique index makes room names case-insensitive.
-- Replace the values before running.
-- insert into public.classrooms (name, capacity, target_temperature, current_temperature, ac_status)
-- values ('Room 301', 45, 22, 22, 'Idle');

-- 3) Create a schedule with an existing teacher and classroom.
-- Replace the values before running. The exclusion constraints reject overlap
-- for either the same teacher or same classroom.
-- insert into public.class_schedules (
--     teacher_account_id, original_teacher_account_id, classroom_id, classroom_name,
--     subject_name, schedule_date, start_time, end_time
-- )
-- select teacher.id, teacher.id, classroom.id, classroom.name,
--        'Introduction to Physics', date '2026-08-24', time '08:00', time '09:30'
-- from public.user_accounts teacher
-- cross join public.classrooms classroom
-- where teacher.username = 'teacher_username_here'
--   and classroom.name = 'Room 301'
--   and teacher.role = 'Teacher'
--   and teacher.is_active = true
--   and classroom.is_active = true;

-- 4) Confirm schedules, including the real teacher assigned and the original
-- teacher when a leave request resulted in substitute assignment.
select schedule.id, schedule.schedule_date, schedule.start_time, schedule.end_time,
       schedule.subject_name, classroom.name as classroom,
       assigned.display_name as assigned_teacher,
       original_teacher.display_name as original_teacher
from public.class_schedules schedule
join public.classrooms classroom on classroom.id = schedule.classroom_id
join public.user_accounts assigned on assigned.id = schedule.teacher_account_id
join public.user_accounts original_teacher on original_teacher.id = coalesce(schedule.original_teacher_account_id, schedule.teacher_account_id)
order by schedule.schedule_date, schedule.start_time;

-- 5) Controlled fresh-start reset. Prefer the Admin dashboard -> System settings
-- -> Reset all system data, because it reseeds the PBKDF2-protected default admin
-- and redirects directly to First Access. If this SQL is required for disaster
-- recovery, run it, then restart SmartClass AC so its startup service reseeds
-- the configured default administrator before opening /first-access.
--
-- begin;
-- truncate table
--   public.temperature_logs, public.attendance_logs, public.teacher_requests,
--   public.support_tickets, public.class_schedules, public.classrooms,
--   public.password_reset_tokens, public.fingerprint_templates, public.user_accounts
-- restart identity cascade;
-- commit;
