import { html, useState } from '../lib.js';
import { api, HttpError } from '../api.js';
import { Field } from './common.js';
import { t } from '../i18n.js';

// Этап C2 (roadmap-next.md): доступно любой роли — каждый меняет свой
// собственный пароль (/api/me/password), не чужой.
export function ChangePasswordView() {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [status, setStatus] = useState('');

  const submit = async e => {
    e.preventDefault();
    try {
      await api('/api/me/password', { method: 'POST', body: { currentPassword: current, newPassword: next } });
      setStatus(t('пароль изменён'));
      setCurrent('');
      setNext('');
    } catch (err) {
      const wrong = err instanceof HttpError && err.body && err.body.error === 'current password is wrong';
      setStatus(wrong ? t('неверный текущий пароль') : t('не удалось сменить пароль (минимум 12 символов)'));
    }
  };

  return html`
    <form class="narrow" onSubmit=${submit}>
      <div class="filters column">
        <${Field} label=${t('Текущий пароль')}><input type="password" required value=${current} onInput=${e => setCurrent(e.target.value)} /><//>
        <${Field} label=${t('Новый пароль (не короче 12 символов)')}><input type="password" minlength="12" required value=${next} onInput=${e => setNext(e.target.value)} /><//>
        <div class="row center spaced">
          <button type="submit">${t('Сменить')}</button>
          <span class="status">${status}</span>
        </div>
      </div>
    </form>`;
}
