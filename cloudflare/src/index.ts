interface Env {
  DB: D1Database;
  ASSETS?: R2Bucket;
  RELEASES?: R2Bucket;
  TOKEN_SECRET: string;
  ALLOWED_ORIGIN: string;
  TOKEN_ISSUER: string;
  LAUNCHER_RELEASE_KEY: string;
  ELY_CLIENT_ID?: string;
  ELY_CLIENT_SECRET?: string;
  ELY_REDIRECT_URI?: string;
}
type Claims = { sub: string; sid: string; iss: string; iat: number; exp: number };
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'content-type':'application/json; charset=utf-8', 'cache-control':'no-store' } });
const b64url = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes)).replace(/=/g,'').replace(/\+/g,'-').replace(/\//g,'_');
const fromB64url = (text: string) => Uint8Array.from(atob(text.replace(/-/g,'+').replace(/_/g,'/') + '='.repeat((4-text.length%4)%4)), c=>c.charCodeAt(0));
const enc = new TextEncoder();
const randomId = () => crypto.randomUUID();
const randomSecret = (size=32) => { const bytes=crypto.getRandomValues(new Uint8Array(size)); return b64url(bytes); };

function validLoopbackCallback(value: string) {
  try { const u=new URL(value); return u.protocol==='http:' && (u.hostname==='127.0.0.1'||u.hostname==='localhost') && !!u.port && u.pathname==='/ely/callback' && !u.username && !u.password && !u.hash; } catch { return false; }
}

async function cleanupExpired(env: Env) {
  try {
    const now = Date.now();
    await env.DB.prepare('DELETE FROM ely_oauth_states WHERE expires_at < ?').bind(now).run();
    await env.DB.prepare('DELETE FROM ely_oauth_tickets WHERE expires_at < ?').bind(now).run();
    await env.DB.prepare('DELETE FROM sessions WHERE expires_at < ?').bind(now).run();
  } catch {}
}

function validatePngGeometry(bytes: Uint8Array, kind: 'skin' | 'cape'): string | null {
  if (bytes.length < 24) return 'Файл повреждён или слишком мал';
  const sig = [137, 80, 78, 71, 13, 10, 26, 10];
  for (let i = 0; i < 8; i++) {
    if (bytes[i] !== sig[i]) return 'Файл не является изображением PNG';
  }
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const width = view.getUint32(16);
  const height = view.getUint32(20);

  if (kind === 'skin') {
    const isValid = (width === 64 && height === 64) || (width === 64 && height === 32) || (width === 128 && height === 128);
    if (!isValid) return `Некорректное разрешение скина (${width}x${height}). Ожидается 64x64 или 64x32.`;
  } else if (kind === 'cape') {
    const isValid = (width === 64 && height === 32) || (width === 22 && height === 17) || (width === 128 && height === 64);
    if (!isValid) return `Некорректное разрешение плаща (${width}x${height}). Ожидается 64x32.`;
  }
  return null;
}

async function encryptTicket(value: unknown, secret: string) {
  const keyBytes=await crypto.subtle.digest('SHA-256',enc.encode(secret));
  const key=await crypto.subtle.importKey('raw',keyBytes,{name:'AES-GCM'},false,['encrypt']);
  const iv=crypto.getRandomValues(new Uint8Array(12));
  const cipher=await crypto.subtle.encrypt({name:'AES-GCM',iv},key,enc.encode(JSON.stringify(value)));
  return {iv:b64url(iv),cipher:b64url(new Uint8Array(cipher))};
}
async function decryptTicket(cipher: string, iv: string, secret: string) {
  const keyBytes=await crypto.subtle.digest('SHA-256',enc.encode(secret));
  const key=await crypto.subtle.importKey('raw',keyBytes,{name:'AES-GCM'},false,['decrypt']);
  const plain=await crypto.subtle.decrypt({name:'AES-GCM',iv:fromB64url(iv)},key,fromB64url(cipher));
  return JSON.parse(new TextDecoder().decode(plain)) as {accessToken:string;username:string;uuid:string;expiresAt:number};
}
function localRedirect(uri: string, values: Record<string,string>) { const target=new URL(uri); for(const [k,v] of Object.entries(values)) target.searchParams.set(k,v); return Response.redirect(target.toString(),302); }

async function signToken(claims: Claims, secret: string) {
  const head = b64url(enc.encode(JSON.stringify({ alg:'HS256', typ:'JWT' })));
  const body = b64url(enc.encode(JSON.stringify(claims)));
  const key = await crypto.subtle.importKey('raw',enc.encode(secret),{name:'HMAC',hash:'SHA-256'},false,['sign']);
  const sig = new Uint8Array(await crypto.subtle.sign('HMAC',key,enc.encode(`${head}.${body}`)));
  return `${head}.${body}.${b64url(sig)}`;
}
async function verifyToken(token: string, env: Env): Promise<Claims|null> {
  const [head,body,sig] = token.split('.'); if (!head || !body || !sig) return null;
  try {
    const key = await crypto.subtle.importKey('raw',enc.encode(env.TOKEN_SECRET),{name:'HMAC',hash:'SHA-256'},false,['verify']);
    const ok = await crypto.subtle.verify('HMAC',key,fromB64url(sig),enc.encode(`${head}.${body}`));
    const claims = JSON.parse(new TextDecoder().decode(fromB64url(body))) as Claims;
    return ok && claims.iss===env.TOKEN_ISSUER && claims.exp>Date.now()/1000 ? claims : null;
  } catch { return null; }
}
async function hashPassword(password: string, salt: Uint8Array) {
  const key = await crypto.subtle.importKey('raw',enc.encode(password),'PBKDF2',false,['deriveBits']);
  return new Uint8Array(await crypto.subtle.deriveBits({name:'PBKDF2',salt,iterations:100_000,hash:'SHA-256'},key,256));
}
async function authenticate(request: Request, env: Env) {
  const token = request.headers.get('authorization')?.replace(/^Bearer\s+/i,''); if (!token) return null;
  const claims = await verifyToken(token,env); if (!claims) return null;
  const session = await env.DB.prepare('SELECT user_id FROM sessions WHERE id=? AND expires_at>?').bind(claims.sid,Date.now()).first<{user_id:string}>();
  return session?.user_id===claims.sub ? claims : null;
}
async function issueSession(userId: string, env: Env) {
  const now = Math.floor(Date.now()/1000), sid = randomId(), exp = now + 60*60*24*30;
  await env.DB.prepare('INSERT INTO sessions(id,user_id,expires_at,created_at) VALUES(?,?,?,?)').bind(sid,userId,exp*1000,now*1000).run();
  const accessToken = await signToken({sub:userId,sid,iss:env.TOKEN_ISSUER,iat:now,exp},env.TOKEN_SECRET);
  return { accessToken, expiresAt:exp };
}
function validateIdentity(email: unknown, username: unknown, password: unknown) {
  if (typeof email!=='string' || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length>254) return 'Некорректный email';
  if (typeof username!=='string' || !/^[A-Za-z0-9_]{3,16}$/.test(username)) return 'Ник: 3–16 символов A–Z, 0–9 или _';
  if (typeof password!=='string' || password.length<8 || password.length>128) return 'Пароль должен содержать не менее 8 символов';
  return null;
}

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url), origin = request.headers.get('origin');
    const isAllowedOrigin = (
      origin === env.ALLOWED_ORIGIN ||
      origin === 'https://kslauncher.com' ||
      origin === 'https://www.kslauncher.com' ||
      origin === 'https://kslauncher.pages.dev' ||
      origin === 'https://kslauncher-com.pages.dev' ||
      (origin && origin.endsWith('.pages.dev'))
    );
    const headers = {
      'access-control-allow-origin': isAllowedOrigin && origin ? origin : (env.ALLOWED_ORIGIN || '*'),
      'access-control-allow-methods': 'GET,POST,PUT,DELETE,OPTIONS',
      'access-control-allow-headers': 'content-type,authorization',
      'vary': 'Origin'
    };
    if (request.method==='OPTIONS') return new Response(null,{status:204,headers});

    // Auto-clean expired records in background
    ctx.waitUntil(cleanupExpired(env));

    const route = async (): Promise<Response> => {
      if (url.pathname==='/v1/health' && request.method==='GET') return json({ok:true});
      if (url.pathname==='/v1/ely/start' && request.method==='GET') {
        if(!env.ELY_CLIENT_ID || !env.ELY_REDIRECT_URI) return json({error:'Вход Ely.by ещё не настроен в Worker'},503);
        const returnUri=url.searchParams.get('return_uri')||'';
        if(!validLoopbackCallback(returnUri)) return json({error:'Некорректный локальный адрес возврата'},400);
        const state=randomSecret(32), expires=Date.now()+10*60_000;
        await env.DB.prepare('INSERT INTO ely_oauth_states(state,return_uri,expires_at) VALUES(?,?,?)').bind(state,returnUri,expires).run();
        const authorize=new URL('https://account.ely.by/oauth2/v1');
        authorize.searchParams.set('client_id',env.ELY_CLIENT_ID); authorize.searchParams.set('redirect_uri',env.ELY_REDIRECT_URI);
        authorize.searchParams.set('response_type','code'); authorize.searchParams.set('scope','account_info minecraft_server_session'); authorize.searchParams.set('state',state);
        const prompt = url.searchParams.get('prompt') || 'select_account';
        authorize.searchParams.set('prompt', prompt);
        return json({authorizationUrl:authorize.toString(),state});
      }
      if (url.pathname==='/v1/ely/callback' && request.method==='GET') {
        const state=url.searchParams.get('state')||'';
        const row=await env.DB.prepare('SELECT return_uri FROM ely_oauth_states WHERE state=? AND expires_at>?').bind(state,Date.now()).first<{return_uri:string}>();
        if(!row || !validLoopbackCallback(row.return_uri)) return json({error:'Сеанс входа истёк. Повторите вход в лаунчере.'},400);
        await env.DB.prepare('DELETE FROM ely_oauth_states WHERE state=?').bind(state).run();
        if(url.searchParams.has('error')) return localRedirect(row.return_uri,{error:'ely_cancelled',state});
        const code=url.searchParams.get('code'); if(!code || !env.ELY_CLIENT_ID || !env.ELY_CLIENT_SECRET || !env.ELY_REDIRECT_URI) return localRedirect(row.return_uri,{error:'ely_failed',state});
        try {
          const form=new URLSearchParams({client_id:env.ELY_CLIENT_ID,client_secret:env.ELY_CLIENT_SECRET,redirect_uri:env.ELY_REDIRECT_URI,grant_type:'authorization_code',code});
          const tokenResponse=await fetch('https://account.ely.by/api/oauth2/v1/token',{method:'POST',headers:{'content-type':'application/x-www-form-urlencoded'},body:form});
          if(!tokenResponse.ok) return localRedirect(row.return_uri,{error:'ely_failed',state});
          const token=await tokenResponse.json() as {access_token?:string;expires_in?:number}; if(!token.access_token) return localRedirect(row.return_uri,{error:'ely_failed',state});
          const infoResponse=await fetch('https://account.ely.by/api/account/v1/info',{headers:{authorization:`Bearer ${token.access_token}`}});
          if(!infoResponse.ok) return localRedirect(row.return_uri,{error:'ely_failed',state});
          const info=await infoResponse.json() as {id?:string;uuid?:string;username?:string;preferredLanguage?:string};
          const username=info.username; const uuid=info.uuid||info.id;
          if(!username || !uuid) return localRedirect(row.return_uri,{error:'ely_failed',state});
          const ticket=randomSecret(32), expiresAt=Date.now()+Math.max(60,Math.min(token.expires_in||3600,86400))*1000;
          const encrypted=await encryptTicket({accessToken:token.access_token,username,uuid,expiresAt},env.TOKEN_SECRET);
          await env.DB.prepare('INSERT INTO ely_oauth_tickets(ticket,payload_ciphertext,payload_iv,expires_at) VALUES(?,?,?,?)').bind(ticket,encrypted.cipher,encrypted.iv,Date.now()+5*60_000).run();
          return localRedirect(row.return_uri,{ticket,state});
        } catch { return localRedirect(row.return_uri,{error:'ely_failed',state}); }
      }
      if (url.pathname==='/v1/ely/exchange' && request.method==='POST') {
        const body=await request.json() as {ticket?:unknown}; if(typeof body.ticket!=='string' || body.ticket.length>128) return json({error:'Некорректный билет входа'},400);
        const row=await env.DB.prepare('SELECT payload_ciphertext,payload_iv FROM ely_oauth_tickets WHERE ticket=? AND expires_at>?').bind(body.ticket,Date.now()).first<{payload_ciphertext:string;payload_iv:string}>();
        if(!row) return json({error:'Билет входа истёк или уже использован'},401);
        await env.DB.prepare('DELETE FROM ely_oauth_tickets WHERE ticket=?').bind(body.ticket).run();
        try { return json(await decryptTicket(row.payload_ciphertext,row.payload_iv,env.TOKEN_SECRET)); }
        catch { return json({error:'Не удалось проверить билет Ely.by'},401); }
      }
      if (url.pathname==='/v1/download' && request.method==='GET') {
        const object=await env.RELEASES?.get(env.LAUNCHER_RELEASE_KEY);
        if(!object) return json({error:'Релиз лаунчера ещё не загружен'},404);
        return new Response(object.body,{headers:{'content-type':'application/octet-stream','content-length':String(object.size),'content-disposition':'attachment; filename="KSLauncher.exe"','cache-control':'public, max-age=300','x-content-type-options':'nosniff'}});
      }
      if (url.pathname==='/v1/auth/register' && request.method==='POST') {
        const body = await request.json() as {email?:unknown;username?:unknown;password?:unknown};
        const error = validateIdentity(body.email,body.username,body.password); if(error) return json({error},400);
        const email = (body.email as string).toLowerCase(), username=body.username as string, salt=crypto.getRandomValues(new Uint8Array(16)), userId=randomId();
        const digest=await hashPassword(body.password as string,salt);
        try { await env.DB.prepare('INSERT INTO users(id,email,username,password_salt,password_hash,created_at) VALUES(?,?,?,?,?,?)').bind(userId,email,username,b64url(salt),b64url(digest),Date.now()).run(); }
        catch { return json({error:'Email или ник уже занят'},409); }
        return json({account:{id:userId,username,uuid:userId.replaceAll('-','')},...(await issueSession(userId,env))},201);
      }
      if (url.pathname==='/v1/auth/login' && request.method==='POST') {
        const body=await request.json() as {email?:string;password?:string};
        if (!body.email || typeof body.password!=='string') return json({error:'Укажите email и пароль'},400);
        const user=await env.DB.prepare('SELECT id,email,username,password_salt,password_hash FROM users WHERE email=?').bind(body.email.toLowerCase()).first<{id:string;email:string;username:string;password_salt:string;password_hash:string}>();
        if(!user) return json({error:'Неверные учётные данные'},401);
        const computed=await hashPassword(body.password,fromB64url(user.password_salt));
        const a=fromB64url(b64url(computed)), b=fromB64url(user.password_hash);
        if(a.length!==b.length) return json({error:'Неверные учётные данные'},401);
        let difference=0; for(let i=0;i<a.length;i++) difference|=a[i]^b[i];
        if(difference!==0) return json({error:'Неверные учётные данные'},401);
        return json({account:{id:user.id,username:user.username,uuid:user.id.replaceAll('-','')},...(await issueSession(user.id,env))});
      }
      if (url.pathname==='/v1/auth/logout' && request.method==='POST') {
        const claims=await authenticate(request,env); if(!claims) return json({error:'Требуется авторизация'},401);
        await env.DB.prepare('DELETE FROM sessions WHERE id=?').bind(claims.sid).run(); return json({ok:true});
      }
      if (url.pathname==='/v1/me' && request.method==='GET') {
        const claims=await authenticate(request,env); if(!claims) return json({error:'Требуется авторизация'},401);
        const user=await env.DB.prepare('SELECT id,username,created_at FROM users WHERE id=?').bind(claims.sub).first<{id:string;username:string;created_at:number}>();
        const items=await env.DB.prepare('SELECT kind,object_key,created_at FROM cosmetics WHERE user_id=?').bind(claims.sub).all<{kind:string;object_key:string;created_at:number}>();
        return json({account:user,cosmetics:items.results.map(x=>({kind:x.kind,url:`/v1/cosmetics/${x.kind}/${claims.sub}`,createdAt:x.created_at}))});
      }
      const upload=url.pathname.match(/^\/v1\/cosmetics\/(skin|cape)$/);
      if(upload && request.method==='PUT') {
        const claims=await authenticate(request,env); if(!claims) return json({error:'Требуется авторизация'},401);
        const type=request.headers.get('content-type')||'', size=Number(request.headers.get('content-length')||0);
        if(type!=='image/png' || size<1 || size>4*1024*1024) return json({error:'Ожидается PNG до 4 МБ'},400);
        const bytes=new Uint8Array(await request.arrayBuffer());
        if(bytes.byteLength>4*1024*1024) return json({error:'Файл больше 4 МБ'},413);

        const kind=upload[1] as 'skin' | 'cape';
        const geoError = validatePngGeometry(bytes, kind);
        if (geoError) return json({error: geoError}, 400);

        let key = `users/${claims.sub}/${kind}.png`;
        if (env.ASSETS) {
          await env.ASSETS.put(key,bytes,{httpMetadata:{contentType:'image/png',cacheControl:'public, max-age=300'}});
        } else {
          let binary = '';
          const len = bytes.byteLength;
          for (let i = 0; i < len; i++) {
            binary += String.fromCharCode(bytes[i]);
          }
          key = `data:image/png;base64,${btoa(binary)}`;
        }
        await env.DB.prepare('INSERT INTO cosmetics(id,user_id,kind,object_key,content_type,created_at) VALUES(?,?,?,?,?,?) ON CONFLICT(user_id,kind) DO UPDATE SET object_key=excluded.object_key,created_at=excluded.created_at').bind(randomId(),claims.sub,kind,key,'image/png',Date.now()).run();
        return json({kind,url:`/v1/cosmetics/${kind}/${claims.sub}`},201);
      }
      const asset=url.pathname.match(/^\/v1\/cosmetics\/(skin|cape)\/([\w-]+)$/);
      if(asset && request.method==='GET') {
        const kind=asset[1], owner=asset[2];
        const row=await env.DB.prepare('SELECT object_key FROM cosmetics WHERE user_id=? AND kind=?').bind(owner,kind).first<{object_key:string}>();
        if(!row) return json({error:'Не найдено'},404);
        if (row.object_key.startsWith('data:image/png;base64,')) {
          const b64 = row.object_key.slice('data:image/png;base64,'.length);
          const binary = atob(b64);
          const bytes = new Uint8Array(binary.length);
          for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
          }
          return new Response(bytes, {headers:{'content-type':'image/png','cache-control':'public, max-age=300','x-content-type-options':'nosniff'}});
        }
        if(!env.ASSETS) return json({error:'Косметика временно недоступна без R2'},503);
        const object=await env.ASSETS.get(row.object_key); if(!object) return json({error:'Не найдено'},404);
        return new Response(object.body,{headers:{'content-type':'image/png','cache-control':'public, max-age=300','x-content-type-options':'nosniff'}});
      }
      // --- РЕАЛЬНАЯ АНАЛИТИКА (TELEMETRY) ---
      if (url.pathname === '/v1/telemetry/event' && request.method === 'POST') {
        try {
          const body = await request.json<any>();
          const eventType = String(body.event_type || 'launch').slice(0, 32);
          const sessionId = String(body.session_id || randomId()).slice(0, 64);
          const version = body.version ? String(body.version).slice(0, 32) : '1.20.4';
          const loader = body.loader ? String(body.loader).slice(0, 32) : 'vanilla';
          const os = body.os ? String(body.os).slice(0, 64) : 'Windows';
          const now = Date.now();

          await env.DB.prepare(`
            CREATE TABLE IF NOT EXISTS telemetry_events (
              id TEXT PRIMARY KEY,
              event_type TEXT NOT NULL,
              session_id TEXT NOT NULL,
              version TEXT,
              loader TEXT,
              os TEXT,
              created_at INTEGER NOT NULL
            )
          `).run();

          await env.DB.prepare(`
            INSERT INTO telemetry_events (id, event_type, session_id, version, loader, os, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
          `).bind(randomId(), eventType, sessionId, version, loader, os, now).run();

          return json({ ok: true, timestamp: now });
        } catch (e: any) {
          return json({ error: e.message }, 500);
        }
      }

      if (url.pathname === '/v1/telemetry/stats' && request.method === 'GET') {
        try {
          await env.DB.prepare(`
            CREATE TABLE IF NOT EXISTS telemetry_events (
              id TEXT PRIMARY KEY,
              event_type TEXT NOT NULL,
              session_id TEXT NOT NULL,
              version TEXT,
              loader TEXT,
              os TEXT,
              created_at INTEGER NOT NULL
            )
          `).run();

          const now = Date.now();
          const dayAgo = now - 24 * 3600 * 1000;
          const weekAgo = now - 7 * 24 * 3600 * 1000;

          const totalRow = await env.DB.prepare('SELECT COUNT(*) as count FROM telemetry_events').first<{ count: number }>();
          const totalEvents = totalRow?.count || 0;

          const uniqueRow = await env.DB.prepare('SELECT COUNT(DISTINCT session_id) as count FROM telemetry_events').first<{ count: number }>();
          const uniqueUsers = uniqueRow?.count || 0;

          const activeTodayRow = await env.DB.prepare('SELECT COUNT(DISTINCT session_id) as count FROM telemetry_events WHERE created_at >= ?').bind(dayAgo).first<{ count: number }>();
          const activeToday = activeTodayRow?.count || 0;

          const activeWeekRow = await env.DB.prepare('SELECT COUNT(DISTINCT session_id) as count FROM telemetry_events WHERE created_at >= ?').bind(weekAgo).first<{ count: number }>();
          const activeWeek = activeWeekRow?.count || 0;

          const versionRows = await env.DB.prepare(`
            SELECT version, COUNT(*) as count 
            FROM telemetry_events 
            WHERE version IS NOT NULL 
            GROUP BY version 
            ORDER BY count DESC 
            LIMIT 5
          `).all<{ version: string; count: number }>();

          const loaderRows = await env.DB.prepare(`
            SELECT loader, COUNT(*) as count 
            FROM telemetry_events 
            WHERE loader IS NOT NULL 
            GROUP BY loader 
            ORDER BY count DESC 
            LIMIT 5
          `).all<{ loader: string; count: number }>();

          const recentRows = await env.DB.prepare(`
            SELECT event_type, version, loader, os, created_at 
            FROM telemetry_events 
            ORDER BY created_at DESC 
            LIMIT 15
          `).all<{ event_type: string; version: string; loader: string; os: string; created_at: number }>();

          return json({
            totalEvents,
            uniqueUsers,
            activeToday,
            activeWeek,
            versions: versionRows.results || [],
            loaders: loaderRows.results || [],
            recentEvents: recentRows.results || []
          });
        } catch (e: any) {
          return json({ error: e.message }, 500);
        }
      }

      if (url.pathname === '/v1/telemetry/reset' && request.method === 'POST') {
        try {
          await env.DB.prepare('DELETE FROM telemetry_events').run();
          return json({ success: true, message: 'История аналитики очищена' });
        } catch (e: any) {
          return json({ error: e.message }, 500);
        }
      }

      // --- КОНТЕНТ ЛАУНЧЕРА И МОНИТОРИНГ СЕРВЕРОВ ---
      if (url.pathname === '/v1/content/home' && request.method === 'GET') {
        try {
          await env.DB.prepare(`
            CREATE TABLE IF NOT EXISTS app_content (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            )
          `).run();

          const row = await env.DB.prepare('SELECT value FROM app_content WHERE key = ?').bind('home_data').first<{ value: string }>();
          if (row && row.value) {
            return json(JSON.parse(row.value));
          }
          return json({
            featuredServer: {
              name: "KS Network · Выживание",
              ip: "play.kslauncher.ru",
              desc: "Официальный сервер KS Network. Версия 1.20.4+"
            },
            news: [
              {
                id: "news-1",
                title: "Глобальный вайп и старт нового сезона!",
                badge: "🔥 НОВОСТЬ ДНЯ",
                desc: "Новая генерация мира, ускоренная прогрузка чанков, автозагрузка модов и обновлённые клановые войны.",
                tags: ["🚀 Все ядра CPU активны", "⚡ Без лагов при создании мира"],
                date: "24 Ноября 2026",
                isFeatured: true
              }
            ]
          });
        } catch (e: any) {
          return json({ error: e.message }, 500);
        }
      }

      if (url.pathname === '/v1/content/home' && request.method === 'POST') {
        try {
          const body = await request.json<any>();
          const now = Date.now();
          await env.DB.prepare(`
            CREATE TABLE IF NOT EXISTS app_content (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            )
          `).run();

          await env.DB.prepare(`
            INSERT INTO app_content (key, value, updated_at)
            VALUES ('home_data', ?, ?)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
          `).bind(JSON.stringify(body), now).run();

          return json({ success: true, updated_at: now });
        } catch (e: any) {
          return json({ error: e.message }, 500);
        }
      }

      return json({error:'Маршрут не найден'},404);
    };
    try {
      const response=await route(); const merged=new Headers(response.headers); for(const [k,v] of Object.entries(headers)) merged.set(k,v);
      return new Response(response.body,{status:response.status,headers:merged});
    } catch (err: any) {
      return json({error: err?.message || 'Внутренняя ошибка сервера'}, 500);
    }
  },
  async scheduled(controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    ctx.waitUntil(cleanupExpired(env));
  }
} satisfies ExportedHandler<Env>;
