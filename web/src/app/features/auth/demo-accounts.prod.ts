// Production stand-in for demo-accounts.ts: same exports, no credentials.

export interface DemoAccount {
  label: string;
  email: string;
}

export const DEMO_PASSWORD = '';

export const DEMO_ACCOUNTS: readonly DemoAccount[] = [];
