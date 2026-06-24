import { test, expect } from '@playwright/test';
import { CustomerJourneyPage } from './support/customer-journey.page';
import { freshCustomerProfile } from './support/test-data';

test.describe('Customer happy path — browse, basket, checkout, real-time notification', () => {
  test('a customer can sign up, add a product, check out, and receive a SignalR push', async ({ page }) => {
    const customerJourney = new CustomerJourneyPage(page);
    const profile = freshCustomerProfile();

    await customerJourney.openStorefront();
    await customerJourney.signUp(profile);

    const addedProductName = await customerJourney.addFirstProductToBasket();
    expect(addedProductName.length, 'Product name should not be empty').toBeGreaterThan(0);

    await customerJourney.openBasket();
    await expect(page.getByTestId('basket-line').first()).toBeVisible();

    await customerJourney.proceedToCheckout();
    await customerJourney.fillCheckoutForm();

    // Orders are identified by a GUID end to end (the storefront shows it as the order reference);
    // there is no separate human order-number sequence in the Ordering service.
    const confirmedOrderReference = await customerJourney.expectOrderConfirmation();
    expect(confirmedOrderReference).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);

    await customerJourney.expectRealtimeNotificationToast();
  });
});
