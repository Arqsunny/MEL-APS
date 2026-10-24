import { useEffect, useState } from 'react';
import { api, setToken } from '../api';
import type { AuthConfig } from '../types';

interface Props { onSignedIn: () => void }

// Local development sign-in for the seeded demo users. It only appears when the API reports mode "dev"
// (Development environment). In production the API validates tokens from the organization's identity provider.
export default function Login({ onSignedIn }: Props) {
  const [cfg, setCfg] = useState<AuthConfig | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.authConfig().then(setCfg).catch(e => setError((e as Error).message));
  }, []);

  async function signIn(username: string) {
    try {
      const { token } = await api.devLogin(username);
      setToken(token);
      onSignedIn();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <div style={{ maxWidth: 440, margin: '12vh auto', padding: 24 }} className="modal-content">
      <h2 style={{ fontSize: 16, marginBottom: 6 }}>Master Equipment List</h2>
      {error && <p className="hint" style={{ color: '#fca5a5' }}>{error}</p>}
      {!cfg && !error && <p className="hint">Loading…</p>}
      {cfg?.mode === 'dev' && (
        <>
          <p className="hint" style={{ marginBottom: 12 }}>
            Development sign-in. Pick a demo user; the API issues a signed token and enforces that user's roles.
          </p>
          {cfg.devUsers?.map(u => (
            <button key={u.username} className="btn btn-secondary" style={{ width: '100%', marginBottom: 8, justifyContent: 'space-between' }}
                    onClick={() => void signIn(u.username)}>
              <span>{u.displayName}<span className="hint"> · {u.username}</span></span>
              <span className="hint">{u.roles.length ? u.roles.join(', ') : 'read only'}</span>
            </button>
          ))}
        </>
      )}
      {cfg?.mode === 'oidc' && (
        <p className="hint">
          Single sign-on is configured on the API, but the browser sign-in (MSAL) is not wired up yet.
          See docs/AUTH.md.
        </p>
      )}
    </div>
  );
}
