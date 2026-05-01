// wwwroot/js/statisticsCharts.js

// Store chart instances to manage them
const charts = {};
let maxDataPoints = 30; // Number of historical data points to show on charts
const DASHBOARD_DATA_POINTS = 30;
const LIVE_CHART_ANIMATION_DURATION = 240;

// Dark theme colors
const GRID_COLOR = 'rgba(255, 255, 255, 0.1)';
const FONT_COLOR = '#94a3b8'; // slate-400

// Default colors for chart lines (updated for dark theme)
const lineColors = [
    '#22d3ee',  // cyan-400
    '#f472b6',  // pink-400
    '#f59e0b',  // amber-400
    '#38bdf8',  // sky-400
    '#a855f7',  // purple-400
    '#fb923c'   // orange-400
];

export function setMaxDataPoints(value) {
    const parsed = Number(value);
    maxDataPoints = Number.isFinite(parsed) ? Math.max(1, Math.round(parsed)) : 30;

    for (const chartId in charts) {
        if (chartId === 'dashboardChart') {
            continue;
        }

        trimChartData(charts[chartId]);
        charts[chartId].update('none');
    }
}

function trimChartData(chart) {
    while (chart.data.labels.length > maxDataPoints) {
        chart.data.labels.shift();
    }

    chart.data.datasets.forEach(dataset => {
        while (dataset.data.length > maxDataPoints) {
            dataset.data.shift();
        }
    });
}

function formatValueForAxis(value, yAxisLabel) {
    if (!Number.isFinite(value)) {
        return 'No data';
    }

    if (yAxisLabel.includes('(Mbps)')) {
        return `${value.toFixed(2)} Mbps`;
    }

    if (yAxisLabel.includes('(ms)')) {
        return `${value.toFixed(0)} ms`;
    }

    if (yAxisLabel.includes('(%)')) {
        return `${value.toFixed(2)}%`;
    }

    return value.toString();
}

// Function to initialize a new chart or update it with initial datasets
export function initializeOrUpdateChart(chartId, yAxisLabel, AdapterIds, adapterNames, initialTimestamp) {
    const ctx = document.getElementById(chartId);
    if (!ctx) {
        console.error(`Chart canvas with ID ${chartId} not found during initialization.`);
        return false; // Indicate failure
    }

    // If chart already exists, destroy it before re-creating
    // This handles cases where initialization might be called multiple times,
    // though the C# side tries to prevent this.
    if (charts[chartId]) {
        console.warn(`Chart ${chartId} already exists. Destroying and re-initializing.`);
        charts[chartId].destroy();
        delete charts[chartId];
    }

    const datasets = AdapterIds.map((AdapterId, index) => ({
        // Use AdapterId for internal tracking, adapterNames for display label
        label: adapterNames[index] || AdapterId,
        AdapterId: AdapterId, // Store original adapter ID for data mapping
        data: [],
        borderColor: lineColors[index % lineColors.length],
        backgroundColor: lineColors[index % lineColors.length].replace('1)', '0.1)'),
        tension: 0.3, // Smooth bezier curves without exaggerated endpoint motion
        cubicInterpolationMode: 'monotone', // Smooth interpolation
        pointRadius: 0,
        pointHoverRadius: 4,
        pointHitRadius: 10,
        borderWidth: 2.5,
        spanGaps: true,
        fill: false
    }));

    try {
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: initialTimestamp ? [initialTimestamp] : [],
                datasets: datasets
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        title: {
                            display: true,
                            text: 'Time'
                        }
                    },
                    y: {
                        beginAtZero: true,
                        title: {
                            display: true,
                            text: yAxisLabel
                        }
                    }
                },
                animation: {
                    duration: LIVE_CHART_ANIMATION_DURATION,
                    easing: 'easeOutCubic'
                },
                animations: {
                    x: {
                        duration: 0
                    },
                    y: {
                        duration: LIVE_CHART_ANIMATION_DURATION,
                        easing: 'easeOutCubic'
                    }
                },
                transitions: {
                    resize: {
                        animation: {
                            duration: 0
                        }
                    }
                },
                plugins: {
                    legend: {
                        display: false  // Disabled - using custom legend in ChartCard.razor
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                        callbacks: {
                            label: function(context) {
                                const label = context.dataset.label || '';
                                const yAxisLabel = context.chart.options.scales.y.title.text || '';
                                return `${label}: ${formatValueForAxis(context.parsed.y, yAxisLabel)}`;
                            }
                        }
                    }
                }
            }
        });
        console.log(`Chart ${chartId} initialized successfully.`);
        return true; // Indicate success
    } catch (error) {
        console.error(`Error creating chart ${chartId}:`, error);
        return false; // Indicate failure
    }
}

// Function to add a new data point to all datasets in a specific chart
export function addDataToChart(chartId, timestamp, dataPointsByAdapterId) {
    // dataPointsByAdapterId is an object: { "adapterID": value1, ... }
    const chart = charts[chartId];
    if (!chart) {
        // This can happen if initialization failed or was called before DOM ready.
        // console.warn(`Chart with ID ${chartId} not found for adding data. Data for timestamp ${timestamp} might be lost for this chart.`);
        return;
    }

    if (chart.data.labels.includes(timestamp) && chart.data.labels.length > 1) {
        // If timestamp already exists and it's not the very first point, update existing points
        // This handles cases where multiple adapters report at slightly different sub-second times
        // but we group them under the same second-level timestamp.
        // console.log(`Updating data for existing timestamp ${timestamp} in chart ${chartId}`);
        const labelIndex = chart.data.labels.indexOf(timestamp);
        chart.data.datasets.forEach(dataset => {
            const adapterId = dataset.AdapterId;
            if (Object.prototype.hasOwnProperty.call(dataPointsByAdapterId, adapterId)) {
                dataset.data[labelIndex] = dataPointsByAdapterId[adapterId];
            }
        });

    } else {
        // Add new timestamp label if it doesn't exist or if it's the first point
        if (!chart.data.labels.includes(timestamp)) {
            chart.data.labels.push(timestamp);
            if (chart.data.labels.length > maxDataPoints) {
                chart.data.labels.shift(); // Remove oldest label
            }
        }

        chart.data.datasets.forEach(dataset => {
            const adapterId = dataset.AdapterId;
            const value = Object.prototype.hasOwnProperty.call(dataPointsByAdapterId, adapterId) ? dataPointsByAdapterId[adapterId] : NaN; // Use NaN for missing data

            dataset.data.push(value);
        });
    }
    trimChartData(chart);
    try {
        chart.update();
    } catch (error) {
        console.error(`Error updating chart ${chartId}:`, error);
    }
}

// Function to dispose of a chart
export function disposeChart(chartId) {
    if (charts[chartId]) {
        charts[chartId].destroy();
        delete charts[chartId];
        console.log(`Chart ${chartId} disposed.`);
    }
}

// Function to initialize a dashboard sparkline chart
export function initializeDashboardSparkline(chartId) {
    const ctx = document.getElementById(chartId);
    if (!ctx) {
        console.error(`Chart canvas with ID ${chartId} not found during initialization.`);
        return false;
    }

    // If chart already exists, destroy it before re-creating
    if (charts[chartId]) {
        console.warn(`Chart ${chartId} already exists. Destroying and re-initializing.`);
        charts[chartId].destroy();
        delete charts[chartId];
    }

    // Generate initial dummy data for sparkline
    const labels = [];
    const data = [];
    const now = Date.now();
    for (let i = DASHBOARD_DATA_POINTS - 1; i >= 0; i--) {
        labels.push('');
        data.push(Math.random() * 100 + 20); // Random data between 20-120
    }

    try {
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    data: data,
                    borderColor: '#22d3ee', // cyan-400
                    backgroundColor: 'rgba(34, 211, 238, 0.1)',
                    tension: 0.4, // Smooth bezier curves
                    cubicInterpolationMode: 'monotone', // Smooth interpolation
                    fill: true,
                    borderWidth: 2,
                    pointRadius: 0,
                    pointHoverRadius: 0
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        display: false,
                        grid: {
                            display: false
                        }
                    },
                    y: {
                        display: false,
                        grid: {
                            display: false
                        }
                    }
                },
                plugins: {
                    legend: {
                        display: false
                    },
                    tooltip: {
                        enabled: false
                    }
                },
                animation: {
                    duration: 0
                }
            }
        });
        console.log(`Dashboard sparkline ${chartId} initialized successfully.`);
        return true;
    } catch (error) {
        console.error(`Error creating dashboard sparkline ${chartId}:`, error);
        return false;
    }
}

// Function to update dashboard sparkline with new data point
export function updateDashboardSparkline(chartId, value) {
    const chart = charts[chartId];
    if (!chart) {
        console.warn(`Chart with ID ${chartId} not found for updating sparkline.`);
        return;
    }

    // Add new data point and remove oldest
    chart.data.datasets[0].data.push(value);
    if (chart.data.datasets[0].data.length > DASHBOARD_DATA_POINTS) {
        chart.data.datasets[0].data.shift();
    }

    chart.data.labels.push('');
    if (chart.data.labels.length > DASHBOARD_DATA_POINTS) {
        chart.data.labels.shift();
    }

    try {
        chart.update('none');
    } catch (error) {
        console.error(`Error updating sparkline ${chartId}:`, error);
    }
}

// Function to dispose all charts
export function disposeAllCharts() {
    for (const chartId in charts) {
        disposeChart(chartId);
    }
    console.log("All charts disposed.");
}
