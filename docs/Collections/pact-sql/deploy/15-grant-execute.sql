/*
  Grants EXECUTE on the V2 procedures to the principal the application's PACTRPT connection uses.
  Replace the placeholder with the grantee shown by 00-preflight.sql for the ORIGINAL procedures. Run after 10- and 11-.
*/
GRANT EXECUTE ON OBJECT::dbo.p4AccountReceivablesV2  TO [<app-login-or-role-placeholder>];
GRANT EXECUTE ON OBJECT::dbo.p32AccountReceivablesV2 TO [<app-login-or-role-placeholder>];
