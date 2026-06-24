import type { Page } from '@playwright/test';
import { expect } from '@playwright/test';

/**
 * Page-object wrapper for the customer journey. Hides selector churn from the spec files —
 * if the SPA renames a button, only this file needs to change. Auth lives behind the header's
 * "Account" dropdown, so the navigation helpers open it first.
 */
export class CustomerJourneyPage {
  constructor(private readonly page: Page) {}

  async openStorefront(): Promise<void> {
    await this.page.goto('/');
    await expect(this.page).toHaveTitle(/FreshCart/i);
  }

  async signUp(profile: { email: string; password: string; displayName: string }): Promise<void> {
    await this.openAccountMenu();
    await this.page.getByRole('link', { name: /create account/i }).click();
    await this.page.getByLabel(/display name/i).fill(profile.displayName);
    await this.page.getByLabel(/email/i).fill(profile.email);
    await this.page.getByLabel(/^password$/i).fill(profile.password);
    await this.page.getByLabel(/confirm password/i).fill(profile.password);
    await this.page.getByRole('button', { name: /create account/i }).click();
    await this.expectAuthenticated();
  }

  async signIn(email: string, password: string): Promise<void> {
    await this.openAccountMenu();
    await this.page.getByRole('link', { name: /sign in/i }).click();
    await this.page.getByLabel(/email/i).fill(email);
    await this.page.getByLabel(/^password$/i).fill(password);
    await this.page.getByRole('button', { name: /sign in/i }).click();
    await this.expectAuthenticated();
  }

  async addFirstProductToBasket(): Promise<string> {
    await this.page.getByRole('link', { name: /catalog/i }).first().click();
    const firstProductCard = this.page.getByTestId('product-card').first();
    await expect(firstProductCard).toBeVisible();
    const productName = (await firstProductCard.getByTestId('product-name').textContent())?.trim() ?? '';
    await firstProductCard.getByRole('button', { name: /add to basket/i }).click();
    // Wait for the add to be confirmed (success toast) before navigating away; otherwise the basket
    // page can load before the POST completes and render the empty state.
    await expect(this.page.getByText(/added to your basket/i)).toBeVisible();
    return productName;
  }

  async openBasket(): Promise<void> {
    await this.page.getByRole('link', { name: 'Basket', exact: true }).click();
    await expect(this.page.getByRole('heading', { name: 'Your basket', exact: true })).toBeVisible();
  }

  async proceedToCheckout(): Promise<void> {
    await this.page.getByRole('link', { name: /proceed to checkout/i }).click();
    await expect(this.page).toHaveURL(/\/checkout/);
  }

  async fillCheckoutForm(): Promise<void> {
    // Payment method defaults to the first option (Credit card); only the required billing
    // address fields need to be supplied. The shipping fieldset stays hidden because
    // "same as billing" is checked by default.
    await this.page.getByLabel('Address line 1', { exact: true }).fill('221B Baker Street');
    await this.page.getByLabel('City', { exact: true }).fill('London');
    await this.page.getByLabel('Postal code', { exact: true }).fill('NW1 6XE');
    await this.page.getByLabel('Country', { exact: true }).selectOption('GB');
    await this.page.getByRole('button', { name: /place order/i }).click();
  }

  async expectOrderConfirmation(): Promise<string> {
    const confirmationHeading = this.page.getByRole('heading', { name: /thank you|order placed|confirmation/i });
    await expect(confirmationHeading).toBeVisible({ timeout: 15_000 });
    const orderReference = await this.page.getByTestId('order-number').textContent();
    return orderReference?.trim() ?? '';
  }

  async expectRealtimeNotificationToast(): Promise<void> {
    // A SignalR push from the Notification service surfaces as a toast as the checkout saga
    // advances; the "Order placed" success toast and the order-status pushes both qualify.
    await expect(this.page.getByTestId('notification-toast').first()).toBeVisible({ timeout: 10_000 });
  }

  private async openAccountMenu(): Promise<void> {
    await this.page.getByTestId('account-menu').click();
  }

  private async expectAuthenticated(): Promise<void> {
    // The "Orders" navigation link is rendered only for an authenticated session and updates
    // reactively, so it is a stable signal that sign-in/sign-up completed. The generous timeout covers
    // a cold first sign-up: Argon2id hashing plus the first EF/SQL round-trip can take ~10-12s on a
    // freshly booted, resource-contended stack.
    await expect(this.page.getByRole('link', { name: 'Orders', exact: true }).first())
      .toBeVisible({ timeout: 30_000 });
  }
}
