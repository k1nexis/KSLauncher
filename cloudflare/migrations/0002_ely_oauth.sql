CREATE TABLE ely_oauth_states (
  state TEXT PRIMARY KEY,
  return_uri TEXT NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE INDEX ely_oauth_states_expiry ON ely_oauth_states(expires_at);
CREATE TABLE ely_oauth_tickets (
  ticket TEXT PRIMARY KEY,
  payload_ciphertext TEXT NOT NULL,
  payload_iv TEXT NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE INDEX ely_oauth_tickets_expiry ON ely_oauth_tickets(expires_at);
