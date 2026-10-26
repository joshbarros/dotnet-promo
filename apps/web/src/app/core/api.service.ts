import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Redemption, LogEntry, Campaign } from './models';

export interface NewCampaign {
  code: string;
  name: string;
  channel: string;
  market: string;
  usesTotal: number;
  discountCents: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  campaigns = () => firstValueFrom(this.http.get<Campaign[]>('/api/campaigns'));
  openCampaign = (body: NewCampaign) =>
    firstValueFrom(this.http.post<Campaign>('/api/campaigns', body));

  redemptions = (campaignCode: string) =>
    firstValueFrom(this.http.get<Redemption[]>('/api/redemptions', { params: { campaignCode } }));
  book = (body: { campaignCode: string; reference: string; uses: number }) =>
    firstValueFrom(this.http.post<Redemption>('/api/redemptions', body));
  revoke = (id: string) =>
    firstValueFrom(this.http.post<Redemption>(`/api/redemptions/${id}/revocation`, {}));
  events = (id: string) =>
    firstValueFrom(this.http.get<LogEntry[]>(`/api/redemptions/${id}/events`));
}
