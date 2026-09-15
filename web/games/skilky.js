/*
  «Скільки?» — компанійська гра на відчуття числа. Клієнт тут нічого не вирішує: усі фази, час і очки
  живуть на сервері (Impl/Skilky.cs), а модуль лише малює те, що прийшло, і шле наміри.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { round, of, phase: 'between'|'ask'|'reveal'|'done', question, unit, endsAt,
      answered: bool[], my: number|null,
      reveal: null | { answer, years, rows: [{ seat, value, diff, points, accuracy, bonus, fast }] },
      scores: number[], result: null | { winners, scores } }
  points = accuracy (за точність) + bonus (найближчому) + fast (швидшому за однакової відстані).
  Кадр (подія 'frame', раз на секунду, летить усій кімнаті — прихованого в ньому нема):
    { round, of, phase, endsAt, answered, scores }
  Хід: Act('answer', { value }) — число або рядок («10 000», «2,54» сервер розбере сам).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M5.2 5.5a2.8 2.8 0 1 1 3.7 2.7c-.7.3-1 .8-1 1.5v.4" fill="none" stroke="var(--accent)" stroke-width="1.9" stroke-linecap="round"/>'
    + '<circle cx="7.9" cy="13" r="1.3" fill="var(--clay)"/></svg>';

  const MS = { between: 3000, ask: 30000, reveal: 6000 };

  /// Шкала очок словами — та сама, що в Skilky.Accuracy на сервері. Міняєш там — міняй і тут.
  const RULES = 'Очки за точність: до 2 % — 5, до 10 % — 4 (промах на одиницю — теж 4), до 25 % — 3, до 50 % — 2, '
    + 'до двох разів — 1. Роки: точно — 5, ±2 — 4, ±5 — 3, ±15 — 2, ±50 — 1. '
    + 'Найближчому +1, навіть коли всі мимо. Однаково близько — швидшому ще +1';

  /// Число для ока: ціле — з пробілами між тисячами, дробове — без хвоста нулів.
  function num(v) {
    if (v == null || isNaN(v)) return '—';
    if (Math.abs(v - Math.round(v)) < 1e-9) return Math.round(v).toLocaleString('uk-UA');
    // Дробове теж українською: людина набирала «2,5», і крапка поруч із «1 000 000» ріже око.
    return v.toLocaleString('uk-UA', { maximumFractionDigits: 3 });
  }

  /// Рік пишемо як рік — «1986», а не «1 986»: розділювач тисяч у році ріже око.
  function yearOr(years, v) {
    return years && v != null && Math.abs(v - Math.round(v)) < 1e-9 ? String(Math.round(v)) : num(v);
  }

  function state(root) {
    if (!root._sk) root._sk = { round: -1 };
    return root._sk;
  }

  /// Кадр свіжіший за вид (він летить щосекунди), але вірити йому можна лише в межах того самого раунду
  /// й фази: після «Ще раз» ctx.frame ще секунду тримає останній кадр минулої партії — з чужим рахунком
  /// і зі старою розсадкою.
  function fresh(ctx, v) {
    const f = ctx.frame;
    return f && f.round === v.round && f.phase === v.phase ? f : null;
  }

  /// Чиї числа вже прийшли.
  function ticks(ctx, v) {
    const f = fresh(ctx, v);
    return (f && f.answered) || v.answered || [];
  }

  /// Місця, на яких хтось сидить. Порожні (стіл на дванадцятьох, грає четверо) не малюємо взагалі.
  function seats(ctx) {
    const room = ctx.room || {};
    const list = [];
    const n = (room.seats && room.seats.length) || 0;
    for (let i = 0; i < n; i++) if (ctx.nickOf(i)) list.push(i);
    return list;
  }

  function paintWho(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.skwho');
    if (!box) return;
    const done = ticks(ctx, v);
    // У лобі склад столу вже видно в шапці картки — другий раз його малювати нема чого.
    const show = ctx.playing && (v.phase === 'ask' || v.phase === 'between');
    const html = show ? seats(ctx).map((i) => '<span class="skchip' + (done[i] ? ' on' : '') + '">'
      + (done[i] ? '✓ ' : '') + ctx.esc(ctx.nickOf(i)) + '</span>').join('') : '';
    if (box.innerHTML !== html) box.innerHTML = html;
  }

  function paintScores(root, ctx) {
    const v = ctx.view || {};
    const f = fresh(ctx, v);
    const sc = (f && f.scores && f.scores.length) ? f.scores : (v.scores || []);
    const box = root.querySelector('.skscore');
    if (!box) return;
    const win = (v.result && v.result.winners) || [];
    // Рахунок з'являється разом із партією: у лобі всі нулі нікому нічого не кажуть.
    const html = !ctx.playing && !v.result ? '' : seats(ctx)
      .slice()
      .sort((a, b) => (sc[b] || 0) - (sc[a] || 0) || a - b)
      .map((i) => '<div class="sksrow' + (win.includes(i) ? ' win' : '') + '">'
        + '<span>' + ctx.esc(ctx.nickOf(i)) + '</span><b>' + (sc[i] || 0) + '</b></div>').join('');
    if (box.innerHTML !== html) box.innerHTML = html;
  }

  /// «Рази» після числа: дробове — «2,5 раза», ціле — «3 рази», «5 разів».
  function times(k) {
    const r = Math.round(k * 10) / 10;
    if (r !== Math.round(r)) return num(r) + ' раза';
    const n = Math.round(r) % 100;
    const word = n % 10 >= 2 && n % 10 <= 4 && (n < 12 || n > 14) ? 'рази' : 'разів';
    return num(Math.round(r)) + ' ' + word;
  }

  /// Як сильно повз — тими ж мірками, якими сервер дає очки: роки в роках, решта у відсотках, а
  /// далеко за межами — у разах (там відсотки вже нічого не кажуть: «на 900 % більше»).
  function missText(r, x) {
    if (Math.abs(x.diff) < 1e-9) return 'точно';
    if (r.years || !(r.answer > 0) || !(x.value > 0)) return 'різниця ' + num(x.diff);
    const more = x.value > r.answer;
    const ratio = more ? x.value / r.answer : r.answer / x.value;
    if (ratio >= 2) return 'у ' + times(ratio) + (more ? ' більше' : ' менше');
    const pct = x.diff / r.answer * 100;
    return 'на ' + num(pct < 10 ? Math.round(pct * 10) / 10 : Math.round(pct)) + ' %' + (more ? ' більше' : ' менше');
  }

  function revealHtml(ctx, v) {
    const r = v.reveal;
    if (!r) return '';
    const rows = r.rows || [];
    const head = '<div class="skans"><span class="muted small">Правильна відповідь</span>'
      + '<b>' + yearOr(r.years, r.answer) + '</b>' + (v.unit ? '<i>' + ctx.esc(v.unit) + '</i>' : '') + '</div>';
    if (!rows.length) return head + '<div class="gempty">Ніхто не назвав жодного числа.</div>';
    return head + '<div class="skrows">' + rows.map((x) => {
      const bonus = x.bonus || 0;
      const fast = x.fast || 0;
      const acc = x.accuracy != null ? x.accuracy : x.points - bonus - fast;
      // Розклад суми показуємо лише тоді, коли є бонуси: «+5» без «5» під ним, але «+6» із «5+1».
      const parts = [acc].concat(bonus ? [bonus] : [], fast ? [fast] : []);
      const why = [acc ? acc + ' за точність' : '', bonus ? bonus + ' найближчому' : '', fast ? fast + ' за швидкість' : '']
        .filter(Boolean).join(', ');
      return '<div class="skrow' + (x.points ? ' on' : '') + (bonus ? ' best' : '') + '">'
        + '<span class="skn">' + (bonus ? '🏆 ' : '') + (fast ? '⚡ ' : '')
        + ctx.esc(ctx.nickOf(x.seat) || ctx.seatName(x.seat)) + '</span>'
        + '<span class="skv">' + yearOr(r.years, x.value) + '</span>'
        + '<span class="skd muted small">' + missText(r, x) + '</span>'
        + '<span class="skp"' + (why ? ' title="' + why + '"' : '') + '>'
        + (x.points ? '+' + x.points : '0')
        + (parts.length > 1 ? '<small>' + parts.join('+') + '</small>' : '')
        + '</span></div>';
    }).join('') + '</div>';
  }

  function answer(root, ctx) {
    const input = root.querySelector('.skin');
    if (!input) return;
    const raw = (input.value || '').trim();
    if (!raw) { ctx.toast('Напиши число', 'err'); input.focus(); return; }
    // Шлемо рядком: «10 000» і «2,54» сервер прочитає сам, а число з input.value і так було б рядком.
    ctx.act('answer', { value: raw });
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const phase = v.phase || 'between';
    const mine = ctx.mine && ctx.playing;

    const no = root.querySelector('.skno');
    const noText = v.of ? 'Питання ' + v.round + ' з ' + v.of : '';
    if (no.textContent !== noText) no.textContent = noText;

    // Дуга живе лише поки партія йде: у лобі та після кінця відліку нема.
    const top = root.querySelector('.sktop');
    if (ctx.playing && v.endsAt && phase !== 'done') {
      HGames.ui.timerArc(top, v.endsAt, MS[phase] || 30000);
    } else {
      const arc = top.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }

    const q = root.querySelector('.skq');
    const qText = phase === 'between' && !v.reveal && !ctx.playing ? 'Стіл зібрався — господар тисне «Почати»'
      : phase === 'between' ? 'Готуйсь…'
      : (v.question || '');
    if (q.textContent !== qText) q.textContent = qText;
    q.classList.toggle('wait', phase === 'between');

    // Шкала очок — поки чекаємо: у лобі й у паузі перед запитанням. Під час відповіді вона лише заважає.
    const rules = root.querySelector('.skrules');
    rules.hidden = !(phase === 'between' && !v.result);

    // Нове запитання — чисте поле: чуже число з минулого раунду там висіти не має.
    const ask = root.querySelector('.skask');
    const input = root.querySelector('.skin');
    if (st.round !== v.round) { st.round = v.round; input.value = ''; }
    const canAsk = mine && phase === 'ask';
    ask.hidden = !canAsk;
    input.disabled = !canAsk;
    const unit = root.querySelector('.skunit');
    const unitText = v.unit || '';
    if (unit.textContent !== unitText) unit.textContent = unitText;
    unit.hidden = !unitText || !canAsk;

    const my = root.querySelector('.skmy');
    const myText = v.my != null && phase === 'ask' ? 'Твоє число: ' + yearOr(v.unit === 'рік', v.my) + '. Можна змінити, поки є час'
      : !ctx.mine && ctx.playing && phase === 'ask' ? 'Дивишся збоку'
      : '';
    if (my.textContent !== myText) my.textContent = myText;

    const rev = root.querySelector('.skrev');
    const html = phase === 'reveal' || phase === 'done' ? revealHtml(ctx, v) : '';
    if (rev.innerHTML !== html) rev.innerHTML = html;

    paintWho(root, ctx);
    paintScores(root, ctx);
  }

  HGames.register({
    id: 'skilky',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      root._sk = { round: -1 };
      // .skmain — усе, що стосується запитання; .skscore — рахунок партії, який на широкій картці
      // від'їжджає праворуч (skilky.css), а на телефоні лягає під таблицю.
      root.innerHTML = '<div class="skwrap">'
        + '<div class="skmain">'
        + '<div class="sktop"><span class="skno muted small"></span></div>'
        + '<div class="skq"></div>'
        + '<div class="skrules muted small" hidden>' + RULES + '</div>'
        + '<div class="skask" hidden><input class="skin" type="text" inputmode="decimal" autocomplete="off"'
        + ' placeholder="твоє число" aria-label="Твоє число"><span class="skunit muted small"></span>'
        + '<button type="button" class="primary skgo">Відповісти</button></div>'
        + '<div class="skmy muted small"></div>'
        + '<div class="skrev"></div>'
        + '<div class="skwho"></div>'
        + '</div>'
        + '<div class="skscore"></div>'
        + '</div>';
      root.querySelector('.skgo').onclick = () => answer(root, ctx);
      root.querySelector('.skin').addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        answer(root, ctx);
      });
      paint(root, ctx);
    },

    update(root, ctx) { paint(root, ctx); },

    /// Кадр не чіпає ні питання, ні розкриття — лише те, що змінюється щосекунди.
    frame(root, ctx) {
      if (!root.querySelector('.skwho')) return;
      paintWho(root, ctx);
      paintScores(root, ctx);
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // Фазу беремо з виду: сервер міняє її тільки разом із видом (TickResult.Both), тож кадр її не
      // випереджає — а от після «Ще раз» кадр ще секунду тримає фазу минулої партії.
      const s = ctx.view || {};
      if (s.phase === 'ask') return ctx.mine ? 'Пиши число й тисни Enter' : 'Гравці думають…';
      if (s.phase === 'reveal') return 'Ось як було насправді';
      if (s.phase === 'between') return 'Зараз буде питання…';
      return '';
    },

    unmount(root) {
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      root._sk = null;
    },
  });
})();
