import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from './core/api.service';
import { Redemption, RedemptionStatus, LogEntry, Campaign } from './core/models';

const BADGES: Record<RedemptionStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Redeemed: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Revoked: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-root',
  imports: [DatePipe],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly campaigns = signal<Campaign[]>([]);
  readonly selectedCode = signal('');
  readonly redemptions = signal<Redemption[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly name = signal('Black Friday 10 Off');
  readonly channel = signal('Email');
  readonly market = signal('US');
  readonly price = signal('1000');
  readonly capamarket = signal('12');

  readonly reference = signal('');
  readonly uses = signal('1');

  readonly selected = computed(() => this.campaigns().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: RedemptionStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const campaigns = await this.api.campaigns();
      this.campaigns.set(campaigns);
      if (!this.selectedCode() && campaigns.length > 0) this.selectedCode.set(campaigns[0].code);
      if (this.selectedCode())
        this.redemptions.set(await this.api.redemptions(this.selectedCode()));
      const id = this.logFor();
      if (id) this.log.set(await this.api.events(id));
    } catch {
      this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openCampaign(): Promise<void> {
    await this.run(async () => {
      const code = this.code();
      const campaign = await this.api.openCampaign({
        code,
        name: this.name(),
        channel: this.channel(),
        market: this.market(),
        usesTotal: Number(this.capamarket()),
        discountCents: Number(this.price()),
      });
      if (this.code() === code) this.code.set('');
      this.selectedCode.set(campaign.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      const reference = this.reference();
      await this.api.book({
        campaignCode: this.selectedCode(),
        reference,
        uses: Number(this.uses()),
      });
      if (this.reference() === reference) this.reference.set('');
    });
  }

  async revoke(redemption: Redemption): Promise<void> {
    await this.run(() => this.api.revoke(redemption.id));
  }

  async showLog(redemption: Redemption): Promise<void> {
    this.logFor.set(redemption.id);
    this.log.set([]);
    await this.refresh();
  }

  canRevoke(redemption: Redemption): boolean {
    return redemption.status === 'Held' || redemption.status === 'Redeemed';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
