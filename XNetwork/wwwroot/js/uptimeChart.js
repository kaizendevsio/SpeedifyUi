// Uptime Chart Module for Connection Health Visualization
import 'https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js';

let uptimeChart = null;
const MAX_DATA_POINTS = 30; // Show last 30 seconds

export function initializeUptimeChart(canvasId) {
    try {
        const canvas = document.getElementById(canvasId);
        if (!canvas) {
            console.error(`Canvas element with id '${canvasId}' not found`);
            return false;
        }

        const ctx = canvas.getContext('2d');
        if (!ctx) {
            console.error('Failed to get 2D context from canvas');
            return false;
        }

        // Destroy existing chart if it exists
        if (uptimeChart) {
            uptimeChart.destroy();
        }

        // Create new chart
        uptimeChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: [],
                datasets: [
                    {
                        label: 'Uptime %',
                        data: [],
                        borderColor: '#10b981', // green-500
                        backgroundColor: 'rgba(16, 185, 129, 0.1)',
                        borderWidth: 2,
                        fill: true,
                        tension: 0.4,
                        pointRadius: 3,
                        pointHoverRadius: 5,
                        pointBackgroundColor: '#10b981',
                        pointBorderColor: '#fff',
                        pointBorderWidth: 1
                    },
                    {
                        label: 'Failures %',
                        data: [],
                        borderColor: '#ef4444', // red-500
                        backgroundColor: 'rgba(239, 68, 68, 0.1)',
                        borderWidth: 2,
                        fill: true,
                        tension: 0.4,
                        pointRadius: 3,
                        pointHoverRadius: 5,
                        pointBackgroundColor: '#ef4444',
                        pointBorderColor: '#fff',
                        pointBorderWidth: 1
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: {
                    mode: 'index',
                    intersect: false
                },
                plugins: {
                    legend: {
                        display: true,
                        position: 'top',
                        labels: {
                            color: '#94a3b8', // slate-400
                            font: {
                                size: 12
                            },
                            usePointStyle: true,
                            padding: 15
                        }
                    },
                    tooltip: {
                        backgroundColor: 'rgba(15, 23, 42, 0.9)', // slate-900
                        titleColor: '#fff',
                        bodyColor: '#cbd5e1', // slate-300
                        borderColor: '#334155', // slate-700
                        borderWidth: 1,
                        padding: 12,
                        displayColors: true,
                        callbacks: {
                            label: function(context) {
                                let label = context.dataset.label || '';
                                if (label) {
                                    label += ': ';
                                }
                                label += context.parsed.y.toFixed(1) + '%';
                                return label;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        display: true,
                        grid: {
                            color: 'rgba(51, 65, 85, 0.3)', // slate-700 with opacity
                            drawBorder: false
                        },
                        ticks: {
                            color: '#64748b', // slate-500
                            font: {
                                size: 10
                            },
                            maxRotation: 0,
                            autoSkip: true,
                            maxTicksLimit: 10
                        }
                    },
                    y: {
                        display: true,
                        min: 0,
                        max: 100,
                        grid: {
                            color: 'rgba(51, 65, 85, 0.3)',
                            drawBorder: false
                        },
                        ticks: {
                            color: '#64748b',
                            font: {
                                size: 10
                            },
                            callback: function(value) {
                                return value + '%';
                            }
                        }
                    }
                },
                animation: {
                    duration: 300
                }
            }
        });

        console.log('Uptime chart initialized successfully');
        return true;
    } catch (error) {
        console.error('Error initializing uptime chart:', error);
        return false;
    }
}

export function updateUptimeChart(timestamp, successRate, failureRate) {
    if (!uptimeChart) {
        console.error('Uptime chart not initialized');
        return;
    }

    try {
        // Add new data point
        uptimeChart.data.labels.push(timestamp);
        uptimeChart.data.datasets[0].data.push(successRate);
        uptimeChart.data.datasets[1].data.push(failureRate);

        // Remove old data points if we exceed the maximum
        if (uptimeChart.data.labels.length > MAX_DATA_POINTS) {
            uptimeChart.data.labels.shift();
            uptimeChart.data.datasets[0].data.shift();
            uptimeChart.data.datasets[1].data.shift();
        }

        // Update the chart
        uptimeChart.update('none'); // Use 'none' mode for better performance
    } catch (error) {
        console.error('Error updating uptime chart:', error);
    }
}

export function disposeUptimeChart() {
    if (uptimeChart) {
        try {
            uptimeChart.destroy();
            uptimeChart = null;
            console.log('Uptime chart disposed');
        } catch (error) {
            console.error('Error disposing uptime chart:', error);
        }
    }
}