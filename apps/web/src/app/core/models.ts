export interface Campaign {
  id: string;
  code: string;
  name: string;
  channel: string;
  market: string;
  endsOn: string;
  discountCents: number;
  usesTotal: number;
  usesLeft: number;
}

export type RedemptionStatus = 'Requested' | 'Held' | 'Redeemed' | 'Revoked' | 'Rejected';

export interface Redemption {
  id: string;
  campaignCode: string;
  reference: string;
  uses: number;
  valueCents: number;
  status: RedemptionStatus;
  reason: string | null;
  couponNumber: string | null;
  createdAt: string;
  updatedAt: string;
  usesReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}
