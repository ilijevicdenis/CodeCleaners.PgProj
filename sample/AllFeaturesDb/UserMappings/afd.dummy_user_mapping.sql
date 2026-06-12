-- User mapping for the dummy foreign server (PUBLIC, so no role is required).
CREATE USER MAPPING FOR PUBLIC SERVER dummy_server OPTIONS (user 'remote_bob');
