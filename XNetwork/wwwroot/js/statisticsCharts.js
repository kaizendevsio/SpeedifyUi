// wwwroot/js/statisticsCharts.js

// Store chart instances to manage them
const charts = {};
const MAX_DATA_POINTS = 30; // Number of historical data points to show on charts

// Default colors for chart lines
const lineColors = [
    'rgba(54, 162, 235, 1)',  // Blue
    'rgba(255, 99, 132, 1)',  // Red
    'rgba(75, 192, 192, 1)',  // Green
    'rgba(255, 206, 86, 1)',  // Yellow
    'rgba(153, 102, 255, 1)', // Purple
    'rgba(255, 159, 64, 1)'   // Orange
];

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
        tension: 0.1,
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
                    duration: 150 // Slightly faster animation
                },
                plugins: {
                    legend: {
                        position: 'top',
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
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
export function addDataToChart(chartId, timestamp, dataPointsByAdapterDisplayName) {
    // dataPointsByAdapterDisplayName is an object: { "ISP (Adapter Name)": value1, ... }
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
            const adapterDisplayName = dataset.label; // Dataset label is the display name
            if (dataPointsByAdapterDisplayName[adapterDisplayName] !== undefined) {
                dataset.data[labelIndex] = dataPointsByAdapterDisplayName[adapterDisplayName];
            }
        });

    } else {
        // Add new timestamp label if it doesn't exist or if it's the first point
        if (!chart.data.labels.includes(timestamp)) {
            chart.data.labels.push(timestamp);
            if (chart.data.labels.length > MAX_DATA_POINTS) {
                chart.data.labels.shift(); // Remove oldest label
            }
        }

        chart.data.datasets.forEach(dataset => {
            const adapterDisplayName = dataset.label; // Dataset label is the display name
            const value = dataPointsByAdapterDisplayName[adapterDisplayName] !== undefined ? dataPointsByAdapterDisplayName[adapterDisplayName] : NaN; // Use NaN for missing data

            dataset.data.push(value);
            if (dataset.data.length > MAX_DATA_POINTS) {
                dataset.data.shift(); // Remove oldest data point
            }
        });
    }
    try {
        chart.update('none'); // 'none' for no animation, 'quiet' for minimal
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

// Function to dispose all charts
export function disposeAllCharts() {
    for (const chartId in charts) {
        disposeChart(chartId);
    }
    console.log("All charts disposed.");
}
